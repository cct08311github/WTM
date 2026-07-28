#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BootstrapBlazor.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using WalkingTec.Mvvm.Core;
using WtmBlazorUtils;

namespace WalkingTec.Mvvm.BlazorDemo.Shared.Test
{
    /// <summary>
    /// Regression test for #852: <c>WTSimpleUpload.razor</c>'s <c>OnAvatarUpload</c> batch-delete
    /// loop interpolated <c>{idstr}</c> -- the whole <c>IEnumerable&lt;string&gt;</c> produced by
    /// <c>files.Select(x =&gt; x.Key)</c> -- instead of the loop variable <c>{id}</c>. Every
    /// <c>DeletedFile</c> call therefore sent the stringified enumerable/iterator (never a real
    /// file GUID) as the id, so this loop never actually deleted anything server-side; only the
    /// in-memory <c>files.Remove(id)</c> bookkeeping worked.
    ///
    /// No project in this repo previously tested a Blazor <c>.razor</c> component directly (there
    /// is no bUnit dependency anywhere in the tree). Rather than pull in a new rendering
    /// framework for one fix, this test uses the same technique already used throughout this repo
    /// for MVC controllers: construct the generated component class directly, wire its
    /// dependencies by hand, invoke the method under test, and observe an actual side effect
    /// (here: the literal HTTP request URI sent through a fake <see cref="HttpMessageHandler"/>)
    /// -- not the component's rendered markup, which this bug does not touch at all.
    /// <c>WtmBlazor</c> is a <c>[Inject]</c>-generated property (protected, per Razor codegen) and
    /// <c>files</c> is a private auto-property backing field; both are set via reflection since
    /// there is no dependency-injection container running in this test.
    /// </summary>
    [TestClass]
    public class WTSimpleUploadBatchDeleteTests852
    {
        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<string> RequestUris { get; } = new();
            public string PreExistingFileId { get; set; } = Guid.NewGuid().ToString();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!.ToString();
                RequestUris.Add(uri);

                // The exact JSON shape FileApiController.Upload/UploadImage return
                // (Ok(new { Id, Name })), which DynamicDataConverter turns into DynamicData.Fields.
                string body = uri.Contains("/api/_file/Upload", StringComparison.OrdinalIgnoreCase)
                    ? $"{{\"Id\":\"{PreExistingFileId}\",\"Name\":\"new-avatar.png\"}}"
                    : "true";

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                return Task.FromResult(response);
            }
        }

        private static IBrowserFile CreateFakeBrowserFile(string name, byte[] payload)
        {
            var mock = new Mock<IBrowserFile>();
            mock.Setup(f => f.Name).Returns(name);
            mock.Setup(f => f.Size).Returns(payload.Length);
            mock.Setup(f => f.OpenReadStream(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns(new MemoryStream(payload));
            return mock.Object;
        }

        private static void SetMember(Type type, object instance, string name, object? value)
        {
            var prop = type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(instance, value);
                return;
            }
            var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(instance, value);
                return;
            }
            throw new InvalidOperationException($"No settable member named '{name}' found on {type}.");
        }

        [TestMethod]
        public async Task OnAvatarUpload_ReplacingExistingFile_DeletesThePreviousFileById_NotTheCollection()
        {
            // Wires CoreProgram.DefaultJsonOption/DefaultPostJsonOption the same way production
            // startup does (WtmBlazorUtils.ServiceExtension.AddWtmBlazor registers
            // DynamicDataConverter among others) -- without this, the fake Upload response can't
            // deserialize into DynamicData at all and the test would fail before ever reaching
            // the code under test.
            var services = new ServiceCollection();
            services.AddWtmBlazor(new Configs());

            var handler = new RecordingHandler();
            var preExistingFileId = handler.PreExistingFileId;
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

            var jsRuntime = new Mock<IJSRuntime>();
            jsRuntime
                .Setup(js => js.InvokeAsync<string>("localStorageFuncs.get", It.IsAny<object?[]>()))
                .Returns(new ValueTask<string>("fake-token-852"));

            var apiClient = new ApiClient(httpClient, jsRuntime.Object);
            var wtmBlazor = new WtmBlazorContext(null!, null!, apiClient, null!, null!, null!, new Configs());

            var componentType = typeof(WTSimpleUpload<>).MakeGenericType(typeof(string));
            var component = Activator.CreateInstance(componentType)!;

            SetMember(componentType, component, "WtmBlazor", wtmBlazor);
            // Seeds `files` with ONE pre-existing upload, as if the user had already uploaded an
            // avatar previously -- this is the file OnAvatarUpload's batch-delete loop is
            // supposed to clean up server-side when it is replaced by a new one.
            SetMember(componentType, component, "files", new Dictionary<string, string> { [preExistingFileId] = "old-avatar.png" });

            var uploadFile = new UploadFile
            {
                File = CreateFakeBrowserFile("new-avatar.png", Encoding.UTF8.GetBytes("fake-image-bytes-852")),
            };

            var method = componentType.GetMethod("OnAvatarUpload", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)method.Invoke(component, new object?[] { uploadFile })!;
            await task;

            var deleteRequests = handler.RequestUris
                .Where(u => u.Contains("/api/_file/DeletedFile/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.AreEqual(1, deleteRequests.Count,
                $"#852: expected exactly one DeletedFile call for the replaced avatar. " +
                $"Requests seen: [{string.Join(", ", handler.RequestUris)}].");
            // Compare the path only (ApiClient.CallAPI always appends a "?culture=..." query
            // string to every request) -- not a suffix/substring match against the raw URI,
            // which the query string would defeat.
            var deletePath = new Uri(deleteRequests[0]).AbsolutePath;
            Assert.AreEqual($"/api/_file/DeletedFile/{preExistingFileId}", deletePath,
                "#852: OnAvatarUpload's batch-delete loop interpolated {idstr} -- the whole " +
                "IEnumerable<string> collection -- instead of the loop variable {id}, so the " +
                "DeletedFile request never actually named the real file id and this loop deleted " +
                $"nothing server-side. Actual request URI: '{deleteRequests[0]}'.");
        }

        /// <summary>
        /// Positive control: <c>OnAvatarDelete</c> (the sibling method the #852 fix does NOT
        /// touch -- it already takes <c>idstr</c> from <c>.FirstOrDefault()</c>, so it is a
        /// single string, not a collection) must still correctly delete by the real file id. This
        /// pins that the harness itself (fake HttpMessageHandler, reflection-based component
        /// construction) can actually observe a CORRECT DeletedFile call when the code takes one
        /// -- proving the negative assertion in the test above is not vacuously true because
        /// nothing in this harness could ever produce a real GUID in the URL.
        /// </summary>
        [TestMethod]
        public async Task OnAvatarDelete_ExistingFile_DeletesByRealId()
        {
            var services = new ServiceCollection();
            services.AddWtmBlazor(new Configs());

            var handler = new RecordingHandler();
            var existingFileId = handler.PreExistingFileId;
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

            var jsRuntime = new Mock<IJSRuntime>();
            jsRuntime
                .Setup(js => js.InvokeAsync<string>("localStorageFuncs.get", It.IsAny<object?[]>()))
                .Returns(new ValueTask<string>("fake-token-852"));

            var apiClient = new ApiClient(httpClient, jsRuntime.Object);
            var wtmBlazor = new WtmBlazorContext(null!, null!, apiClient, null!, null!, null!, new Configs());

            var componentType = typeof(WTSimpleUpload<>).MakeGenericType(typeof(string));
            var component = Activator.CreateInstance(componentType)!;

            SetMember(componentType, component, "WtmBlazor", wtmBlazor);
            SetMember(componentType, component, "files", new Dictionary<string, string> { [existingFileId] = "avatar.png" });

            // UploadFile.OriginFileName has a private setter (populated internally by
            // BootstrapBlazor's own upload pipeline in production) -- set it via reflection like
            // the component's own members above, rather than an inaccessible object initializer.
            var deleteTarget = new UploadFile();
            SetMember(typeof(UploadFile), deleteTarget, "OriginFileName", "avatar.png");

            var method = componentType.GetMethod("OnAvatarDelete", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task<bool>)method.Invoke(component, new object?[] { deleteTarget })!;
            await task;

            var deleteRequests = handler.RequestUris
                .Where(u => u.Contains("/api/_file/DeletedFile/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.AreEqual(1, deleteRequests.Count,
                $"positive control: expected exactly one DeletedFile call. Requests seen: [{string.Join(", ", handler.RequestUris)}].");
            var deletePath = new Uri(deleteRequests[0]).AbsolutePath;
            Assert.AreEqual($"/api/_file/DeletedFile/{existingFileId}", deletePath,
                "positive control: OnAvatarDelete must call DeletedFile with the real file id -- " +
                "proving this test harness can observe a correct call, so the negative assertion " +
                $"in the OnAvatarUpload test above is meaningful. Actual request URI: '{deleteRequests[0]}'.");
        }
    }
}
