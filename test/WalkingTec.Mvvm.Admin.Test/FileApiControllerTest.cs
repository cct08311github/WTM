using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Mvc.Filters;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>The template file API keeps the shape that the framework's helper and the single page application rely on.</summary>
    [TestClass]
    public class FileApiControllerTest
    {
        private static readonly string[] Reads = { "GetFileName", "GetFile", "GetFileInfo", "GetUserPhoto", "DownloadFile" };

        private static MethodInfo[] Actions() => typeof(FileApiController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        [TestMethod]
        public void EveryAction_DeclaresItselfAFileEndpoint_SoTheStartupCheckStaysQuiet()
        {
            foreach (var action in Actions())
            {
                Assert.IsNotNull(action.GetCustomAttribute<FileEndpointAttribute>(), action.Name);
            }
            Assert.AreEqual(0, SpaFileApi.FindUnpatchedControllers(new[] { typeof(FileApiController) }).Count);
        }

        [TestMethod]
        public void TheReadActions_AreOpenToCallersWhoAreNotSignedIn_TheWritesAreNot()
        {
            foreach (var action in Actions())
            {
                var isPublic = action.GetCustomAttribute<PublicAttribute>() != null;
                Assert.AreEqual(Reads.Contains(action.Name), isPublic, action.Name);
                Assert.AreEqual(Reads.Contains(action.Name), action.GetCustomAttribute<FileEndpointAttribute>().PublicWhenFilesPublic, action.Name);
                Assert.AreEqual(Reads.Contains(action.Name), PrivilegeFilter.IsOpenFileApiAction(typeof(FileApiController), action), action.Name);
            }
        }

        [TestMethod]
        public void EveryAction_TakesTheReferenceAndTheConnection()
        {
            foreach (var action in Actions().Where(x => x.Name != "ResizeImage"))
            {
                var names = action.GetParameters().Select(x => x.Name).ToList();
                CollectionAssert.Contains(names, "_DONOT_USE_FREF", action.Name);
                CollectionAssert.Contains(names, "csName", action.Name);
            }
        }

        [TestMethod]
        public void DeletedFile_AcceptsGetPostAndDelete_AndTheEditorHasItsOwnUpload()
        {
            var methods = typeof(FileApiController).GetMethod("DeletedFile").GetCustomAttributes().OfType<IActionHttpMethodProvider>().SelectMany(x => x.HttpMethods).ToList();
            CollectionAssert.AreEquivalent(new[] { "GET", "POST", "DELETE" }, methods);
            Assert.IsNotNull(typeof(FileApiController).GetMethod("UploadForEditor"));
            CollectionAssert.AreEqual(new[] { "POST" }, typeof(FileApiController).GetMethod("UploadForEditor").GetCustomAttributes().OfType<IActionHttpMethodProvider>().SelectMany(x => x.HttpMethods).ToList());
        }
    }
}
