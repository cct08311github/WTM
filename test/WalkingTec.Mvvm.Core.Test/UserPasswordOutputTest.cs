using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Json;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// FrameworkUserBase.Password is left out of JSON responses by the framework's default JSON options,
    /// while a request body that contains it still binds (create user, change password).
    /// </summary>
    [TestClass]
    public class UserPasswordOutputTest
    {
        public class UserWrapper
        {
            public LoginTestUser Entity { get; set; }
        }

        public class OtherPasswordHolder
        {
            public string Name { get; set; }
            public string Password { get; set; }
        }

        public class DerivedTestUser : LoginTestUser
        {
            public string Nick { get; set; }
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

        // UseWtmJsonOptions is what an application calls in AddJsonOptions; it also publishes CoreProgram.DefaultJsonOption.
        private static JsonSerializerOptions WtmOptions()
        {
            var options = new JsonOptions();
            options.UseWtmJsonOptions();
            return options.JsonSerializerOptions;
        }

        [TestMethod]
        public void Serialize_User_OmitsPassword()
        {
            var user = new LoginTestUser { ITCode = "tom", Name = "Tom", Password = "stored-value" };

            var json = JsonSerializer.Serialize(user, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            Assert.IsFalse(doc.RootElement.TryGetProperty("Password", out _), json);
            Assert.IsFalse(json.Contains("stored-value"), json);
            Assert.AreEqual("tom", doc.RootElement.GetProperty("ITCode").GetString());
            Assert.AreEqual("Tom", doc.RootElement.GetProperty("Name").GetString());
        }

        [TestMethod]
        public void Serialize_UserInsideViewModelShape_OmitsPassword()
        {
            var vm = new UserWrapper { Entity = new LoginTestUser { ITCode = "tom", Name = "Tom", Password = "stored-value" } };

            var json = JsonSerializer.Serialize(vm, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            var entity = doc.RootElement.GetProperty("Entity");
            Assert.IsFalse(entity.TryGetProperty("Password", out _), json);
            Assert.AreEqual("tom", entity.GetProperty("ITCode").GetString());
        }

        [TestMethod]
        public void Serialize_SubclassOfUser_OmitsPassword()
        {
            var user = new DerivedTestUser { ITCode = "tom", Name = "Tom", Password = "stored-value", Nick = "T" };

            var json = JsonSerializer.Serialize(user, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            Assert.IsFalse(doc.RootElement.TryGetProperty("Password", out _), json);
            Assert.AreEqual("T", doc.RootElement.GetProperty("Nick").GetString());
        }

        [TestMethod]
        public void Serialize_ThroughDefaultJsonOption_OmitsPassword()
        {
            WtmOptions();

            var json = JsonSerializer.Serialize(new LoginTestUser { ITCode = "tom", Name = "Tom", Password = "stored-value" }, CoreProgram.DefaultJsonOption);

            Assert.IsFalse(json.Contains("Password"), json);
        }

        [TestMethod]
        public void Serialize_ThroughRequestOptionsOfOutgoingCalls_StillWritesPassword()
        {
            // DefaultPostJsonOption serializes what the framework sends to another service (WTMContext.CallAPI): not a response
            WtmOptions();

            var json = JsonSerializer.Serialize(new LoginTestUser { ITCode = "tom", Name = "Tom", Password = "sent-value" }, CoreProgram.DefaultPostJsonOption);

            Assert.IsTrue(json.Contains("sent-value"), json);
        }

        [TestMethod]
        public void Deserialize_JsonWithPassword_StillSetsPassword()
        {
            const string body = "{\"ITCode\":\"tom\",\"Name\":\"Tom\",\"Password\":\"new-value\"}";

            var user = JsonSerializer.Deserialize<LoginTestUser>(body, WtmOptions());

            Assert.AreEqual("new-value", user.Password);
            Assert.AreEqual("tom", user.ITCode);
        }

        [TestMethod]
        public void Deserialize_ViewModelShapeWithPassword_StillSetsPassword()
        {
            const string body = "{\"Entity\":{\"ITCode\":\"tom\",\"Name\":\"Tom\",\"Password\":\"new-value\"}}";

            var vm = JsonSerializer.Deserialize<UserWrapper>(body, WtmOptions());

            Assert.AreEqual("new-value", vm.Entity.Password);
        }

        [TestMethod]
        public void Deserialize_AfterSerialize_DoesNotEchoPassword()
        {
            var options = WtmOptions();
            var written = JsonSerializer.Serialize(new LoginTestUser { ITCode = "tom", Name = "Tom", Password = "stored-value" }, options);

            var read = JsonSerializer.Deserialize<LoginTestUser>(written, options);

            Assert.IsNull(read.Password);
        }

        [TestMethod]
        public void Serialize_OtherTypeWithPasswordProperty_IsNotAffected()
        {
            var json = JsonSerializer.Serialize(new OtherPasswordHolder { Name = "x", Password = "kept" }, WtmOptions());

            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual("kept", doc.RootElement.GetProperty("Password").GetString());
        }

        [TestMethod]
        public void ExcludeUserPasswordFromOutput_KeepsExistingResolverModifiers()
        {
            var options = new JsonSerializerOptions();
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                .WithAddedModifier(t =>
                {
                    if (t.Type == typeof(OtherPasswordHolder))
                    {
                        foreach (var p in t.Properties)
                        {
                            if (p.Name == "Name") p.ShouldSerialize = static (_, _) => false;
                        }
                    }
                });
            options.ExcludeUserPasswordFromOutput();

            var other = JsonSerializer.Serialize(new OtherPasswordHolder { Name = "x", Password = "kept" }, options);
            var user = JsonSerializer.Serialize(new LoginTestUser { ITCode = "tom", Name = "Tom", Password = "stored-value" }, options);

            Assert.IsFalse(other.Contains("\"Name\""), other);
            Assert.IsTrue(other.Contains("kept"), other);
            Assert.IsFalse(user.Contains("Password"), user);
        }
    }
}
