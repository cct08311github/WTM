using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The storage details of a FileAttachment (Path, ExtraInfo, SaveMode, HandlerInfo and the bytes) are left out of JSON responses by the framework's
    /// default JSON options, together with what a client needs (Id, FileName, FileExt, Length, UploadTime); JSON input and server side code are not affected.
    /// </summary>
    [TestClass]
    public class FileAttachmentOutputTest
    {
        private static readonly string[] Hidden = { "Path", "ExtraInfo", "SaveMode", "HandlerInfo", "FileData" };

        public class DerivedAttachment : FileAttachment
        {
            public string Note { get; set; }
        }

        public class Holder
        {
            public FileAttachment Photo { get; set; }
            public Student Entity { get; set; }
        }

        public class InterfaceHolder
        {
            public IWtmFile File { get; set; }
            public List<IWtmFile> Files { get; set; }
        }

        public class OtherPathHolder
        {
            public string Path { get; set; }
            public string SaveMode { get; set; }
        }

        private JsonSerializerOptions _previousDefault;
        private JsonSerializerOptions _previousPost;

        [TestInitialize]
        public void Init()
        {
            _previousDefault = CoreProgram.DefaultJsonOption;
            _previousPost = CoreProgram.DefaultPostJsonOption;
        }

        [TestCleanup]
        public void Cleanup()
        {
            CoreProgram.DefaultJsonOption = _previousDefault;
            CoreProgram.DefaultPostJsonOption = _previousPost;
        }

        private static JsonSerializerOptions WtmOptions()
        {
            var options = new JsonOptions();
            options.UseWtmJsonOptions();
            return options.JsonSerializerOptions;
        }

        private static FileAttachment Stored() => new FileAttachment
        {
            ID = Guid.NewGuid(),
            FileName = "photo.png",
            FileExt = "png",
            Length = 1234,
            UploadTime = new DateTime(2026, 5, 6, 7, 8, 9),
            Path = "secret/dir/photo.png",
            ExtraInfo = "wtm:embedded;extra-value",
            SaveMode = "local",
            HandlerInfo = "handler-value",
            FileData = new byte[] { 1, 2, 3, 4 },
            TenantCode = "t1"
        };

        private static void AssertOnlyClientFields(JsonElement file, FileAttachment source)
        {
            foreach (var name in Hidden)
            {
                Assert.IsFalse(file.TryGetProperty(name, out _), name + " was written: " + file);
            }
            Assert.AreEqual(source.ID.ToString(), file.GetProperty("ID").GetString());
            Assert.AreEqual("photo.png", file.GetProperty("FileName").GetString());
            Assert.AreEqual("png", file.GetProperty("FileExt").GetString());
            Assert.AreEqual(1234, long.Parse(file.GetProperty("Length").GetString()));
            Assert.IsTrue(file.TryGetProperty("UploadTime", out _));
        }

        [TestMethod]
        public void Serialize_FileAttachment_KeepsWhatAClientNeeds_AndNoStorageDetails()
        {
            var file = Stored();

            var json = JsonSerializer.Serialize(file, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            AssertOnlyClientFields(doc.RootElement, file);
            foreach (var value in new[] { "secret/dir", "extra-value", "handler-value", "local", "AQIDBA==" })
            {
                Assert.IsFalse(json.Contains(value), json);
            }
        }

        [TestMethod]
        public void Serialize_FileAttachmentInsideAViewModelShape_WritesNoStorageDetails()
        {
            var file = Stored();
            var holder = new Holder { Photo = file, Entity = new Student { LoginName = "a", Name = "n", Password = "p", PhotoId = file.ID, Photo = file } };

            var json = JsonSerializer.Serialize(holder, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            AssertOnlyClientFields(doc.RootElement.GetProperty("Photo"), file);
            AssertOnlyClientFields(doc.RootElement.GetProperty("Entity").GetProperty("Photo"), file);
            Assert.AreEqual("a", doc.RootElement.GetProperty("Entity").GetProperty("LoginName").GetString());
        }

        [TestMethod]
        public void Serialize_SubclassOfFileAttachment_WritesNoStorageDetails_ButItsOwnMembers()
        {
            var file = new DerivedAttachment { ID = Guid.NewGuid(), FileName = "photo.png", FileExt = "png", Length = 1234, UploadTime = DateTime.Now, Path = "p/x", FileData = new byte[] { 9 }, Note = "mine" };

            var json = JsonSerializer.Serialize(file, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            Assert.IsFalse(doc.RootElement.TryGetProperty("Path", out _), json);
            Assert.IsFalse(doc.RootElement.TryGetProperty("FileData", out _), json);
            Assert.AreEqual("mine", doc.RootElement.GetProperty("Note").GetString());
        }

        [TestMethod]
        public void Serialize_PropertyDeclaredAsIWtmFile_WritesNoStorageDetails()
        {
            var file = Stored();
            file.DataStream = new MemoryStream(new byte[] { 5, 6, 7 });
            var holder = new InterfaceHolder { File = file, Files = new List<IWtmFile> { Stored() } };

            var json = JsonSerializer.Serialize(holder, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            foreach (var item in new[] { doc.RootElement.GetProperty("File"), doc.RootElement.GetProperty("Files")[0] })
            {
                foreach (var name in Hidden.Concat(new[] { "DataStream" }))
                {
                    Assert.IsFalse(item.TryGetProperty(name, out _), name + " was written: " + item);
                }
                Assert.AreEqual("photo.png", item.GetProperty("FileName").GetString());
                Assert.AreEqual("png", item.GetProperty("FileExt").GetString());
                Assert.AreEqual(1234, long.Parse(item.GetProperty("Length").GetString()));
                Assert.IsTrue(item.TryGetProperty("UploadTime", out _));
            }
            foreach (var value in new[] { "secret/dir", "extra-value", "handler-value", "local" })
            {
                Assert.IsFalse(json.Contains(value), json);
            }
        }

        [TestMethod]
        public void Serialize_ThroughAnObjectTypedProperty_WritesNoStorageDetails()
        {
            var json = JsonSerializer.Serialize(new { Value = (object)Stored() }, WtmOptions());

            foreach (var name in Hidden)
            {
                Assert.IsFalse(json.Contains("\"" + name + "\""), json);
            }
        }

        [TestMethod]
        public void Serialize_ThroughDefaultJsonOption_WritesNoStorageDetails()
        {
            WtmOptions();

            var json = JsonSerializer.Serialize(Stored(), CoreProgram.DefaultJsonOption);

            foreach (var name in Hidden)
            {
                Assert.IsFalse(json.Contains("\"" + name + "\""), json);
            }
        }

        [TestMethod]
        public void TheDistributedCache_UsesTheSameOptions_SoACachedFileAttachmentHasNoStorageDetailsEither()
        {
            // objects the framework or an application keeps in the cache are written with the shared options
            WtmOptions();
            var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

            cache.Add("file-attachment-test", Stored());
            var back = cache.Get<FileAttachment>("file-attachment-test");

            Assert.AreEqual("photo.png", back.FileName);
            Assert.AreEqual(1234, back.Length);
            Assert.IsNull(back.Path);
            Assert.IsNull(back.SaveMode);
            Assert.IsNull(back.FileData);
        }

        [TestMethod]
        public void Serialize_ThroughRequestOptionsOfOutgoingCalls_IsNotChanged()
        {
            // DefaultPostJsonOption serializes what the framework sends to another service: not a response
            WtmOptions();

            var json = JsonSerializer.Serialize(Stored(), CoreProgram.DefaultPostJsonOption);

            StringAssert.Contains(json, "\"Path\"");
        }

        [TestMethod]
        public void Serialize_OtherTypesWithTheSameMemberNames_AreNotAffected()
        {
            var json = JsonSerializer.Serialize(new OtherPathHolder { Path = "kept", SaveMode = "kept-too" }, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual("kept", doc.RootElement.GetProperty("Path").GetString());
            Assert.AreEqual("kept-too", doc.RootElement.GetProperty("SaveMode").GetString());
        }

        [TestMethod]
        public void Deserialize_JsonWithStorageMembers_StillBinds()
        {
            const string body = "{\"FileName\":\"a.png\",\"Path\":\"in/path\",\"SaveMode\":\"local\",\"ExtraInfo\":\"x\"}";

            var file = JsonSerializer.Deserialize<FileAttachment>(body, WtmOptions());

            Assert.AreEqual("a.png", file.FileName);
            Assert.AreEqual("in/path", file.Path);
            Assert.AreEqual("local", file.SaveMode);
            Assert.AreEqual("x", file.ExtraInfo);
        }

        [TestMethod]
        public void TheEntity_KeepsItsValues_ForServerSideCode()
        {
            var file = Stored();

            JsonSerializer.Serialize(file, WtmOptions());

            Assert.AreEqual("secret/dir/photo.png", file.Path);
            Assert.AreEqual("local", file.SaveMode);
            Assert.AreEqual("wtm:embedded;extra-value", file.ExtraInfo);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, file.FileData);
        }

        [TestMethod]
        public void ExcludeFileStorageDetailsFromOutput_KeepsExistingResolverModifiers()
        {
            var options = new JsonSerializerOptions();
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                .WithAddedModifier(t =>
                {
                    if (t.Type == typeof(OtherPathHolder))
                    {
                        foreach (var p in t.Properties.Where(p => p.Name == "SaveMode"))
                        {
                            p.ShouldSerialize = static (_, _) => false;
                        }
                    }
                });
            options.ExcludeFileStorageDetailsFromOutput();

            var other = JsonSerializer.Serialize(new OtherPathHolder { Path = "kept", SaveMode = "hidden-by-the-first-modifier" }, options);
            var file = JsonSerializer.Serialize(Stored(), options);

            Assert.IsFalse(other.Contains("SaveMode"), other);
            Assert.IsTrue(other.Contains("kept"), other);
            Assert.IsFalse(file.Contains("\"Path\""), file);
        }

        // ---- the detail view model of a record that has a file --------------------------------------------------------------------------

        [TestMethod]
        public void TheDetailOfARecordWithAPhoto_WritesNoStorageDetails_ButServerSideCodeStillReadsThem()
        {
            var seed = Guid.NewGuid().ToString();
            var photoId = Guid.NewGuid();
            var studentId = Guid.NewGuid();
            using (var dc = new DataContext(seed, DBTypeEnum.Memory))
            {
                dc.Set<FileAttachment>().Add(new FileAttachment
                {
                    ID = photoId, FileName = "photo.png", FileExt = "png", Length = 1234, UploadTime = new DateTime(2026, 5, 6),
                    Path = "secret/dir/photo.png", ExtraInfo = "extra-value", SaveMode = "local", FileData = new byte[] { 1, 2, 3 }
                });
                dc.Set<Student>().Add(new Student { ID = studentId, LoginName = "s", Password = "p", Name = "n", IsValid = true, PhotoId = photoId });
                dc.SaveChanges();
            }
            var wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "alice");
            var vm = new BaseCRUDVM<Student> { Wtm = wtm };

            vm.SetEntityById(studentId.ToString());

            // what server side code (the file provider, import view models) reads
            Assert.AreEqual("secret/dir/photo.png", vm.Entity.Photo.Path);
            Assert.AreEqual("local", vm.Entity.Photo.SaveMode);
            Assert.AreEqual("extra-value", vm.Entity.Photo.ExtraInfo);

            // what the response says
            var json = JsonSerializer.Serialize(vm, WtmOptions());
            using var doc = JsonDocument.Parse(json);
            var photo = doc.RootElement.GetProperty("Entity").GetProperty("Photo");
            AssertOnlyClientFields(photo, vm.Entity.Photo);
            foreach (var value in new[] { "secret/dir", "extra-value" })
            {
                Assert.IsFalse(json.Contains(value), json);
            }
        }
    }
}
