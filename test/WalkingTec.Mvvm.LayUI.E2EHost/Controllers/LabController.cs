using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.LayUI.E2EHost.Fixtures;
using WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs;
using WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.LabVMs;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Controllers
{
    /// <summary>
    /// Fixture pages for the LayUI tag helpers (test only). Every page shows one group of tag helpers with synthetic data,
    /// including text that needs HTML / JavaScript encoding. The pages post to <see cref="Save"/>, which keeps the raw form
    /// of the last post per page so that the browser tests can read back exactly what the browser sent.
    /// Reachable by every signed in user (AllRights); the menu entries are seeded in DataContext.
    /// </summary>
    [ActionDescription("Lab")]
    public class LabController : BaseController
    {
        private static readonly ConcurrentDictionary<string, Dictionary<string, string[]>> LastPosts = new();

        // ---- pages -------------------------------------------------------------------------------------------------

        [AllRights]
        [ActionDescription("Inputs")]
        public ActionResult Inputs()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            Seed(vm);
            return PartialView(vm);
        }

        [AllRights]
        [ActionDescription("Choices")]
        public ActionResult Choices()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            Seed(vm);
            return PartialView(vm);
        }

        [AllRights]
        [ActionDescription("Pickers")]
        public ActionResult Pickers()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            return PartialView(vm);
        }

        [AllRights]
        [ActionDescription("Layout")]
        public ActionResult Layout()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            Seed(vm);
            return PartialView(vm);
        }

        [AllRights]
        [ActionDescription("Detail")]
        public ActionResult Detail()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            return PartialView(vm);
        }

        [AllRights]
        [ActionDescription("Charts")]
        public ActionResult Charts()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            return PartialView(vm);
        }

        [AllRights]
        [ActionDescription("Trees")]
        public ActionResult Trees()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            Seed(vm);
            return PartialView(vm);
        }

        [AllRights]
        [ActionDescription("Grid")]
        public ActionResult Grid()
        {
            var vm = Wtm.CreateVM<LabItemListVM>();
            return PartialView(vm);
        }

        [AllRights]
        [ActionDescription("Search")]
        [HttpPost]
        public string Search(LabItemSearcher searcher)
        {
            var vm = Wtm.CreateVM<LabItemListVM>(passInit: true);
            if (ModelState.IsValid)
            {
                vm.Searcher = searcher;
                return vm.GetJson(false);
            }
            return vm.GetError();
        }

        /// <summary>Answers the posted ids as a small text file (target of the "Download selected" button of the Lab grid).</summary>
        [AllRights]
        [HttpPost]
        [ActionDescription("Sample")]
        public IActionResult Sample(string[] Ids)
        {
            var text = "ids=" + (Ids?.Length ?? 0);
            return File(System.Text.Encoding.UTF8.GetBytes(text), "text/plain", "sample.txt");
        }

        /// <summary>The content pane of the tree container that loads pages (auto-load).</summary>
        [AllRights]
        [ActionDescription("Pane")]
        public ActionResult Pane(string id)
        {
            return Content("<div id=\"lab-pane\" style=\"padding:10px\">pane " + System.Web.HttpUtility.HtmlEncode(id) + "</div>", "text/html");
        }

        [AllRights]
        [ActionDescription("Details")]
        public ActionResult Details(string id)
        {
            var vm = Wtm.CreateVM<LabItemVM>(id);
            return PartialView(vm);
        }

        // ---- form post ---------------------------------------------------------------------------------------------

        /// <summary>Records the raw form (what the browser sent) and answers with the framework's alert.</summary>
        [AllRights]
        [HttpPost]
        [ActionDescription("Save")]
        public ActionResult Save(LabFormVM vm, string page = "Inputs")
        {
            Record(page);
            if (!ModelState.IsValid)
            {
                vm.DoReInit();
                return PartialView(page, vm);
            }
            return FFResult().Alert("Saved " + page);
        }

        /// <summary>Records the raw form and renders the same page again from the posted values (the validation-failure path of a form).</summary>
        [AllRights]
        [HttpPost]
        [ActionDescription("Reflect")]
        public ActionResult Reflect(LabFormVM vm, string page = "Inputs")
        {
            Record(page);
            vm.DoReInit();
            return PartialView(page, vm);
        }

        [AllRights]
        [ActionDescription("LastPost")]
        public ActionResult LastPost(string page = "Inputs")
        {
            return Json(LastPosts.TryGetValue(page, out var form) ? form : new Dictionary<string, string[]>());
        }

        // ---- data for linked / remote fields, charts ---------------------------------------------------------------

        [AllRights]
        [ActionDescription("Cities")]
        public ActionResult Cities(string[] id)
        {
            var rv = new List<ComboSelectListItem>();
            if (id != null && id.Contains("north"))
            {
                rv.Add(new ComboSelectListItem { Value = "n1", Text = "North city" });
                rv.Add(new ComboSelectListItem { Value = "n2", Text = LabText.SpecialItem });
            }
            if (id != null && id.Contains("south"))
            {
                rv.Add(new ComboSelectListItem { Value = "s1", Text = "South city" });
            }
            return JsonMore(rv);
        }

        [AllRights]
        [ActionDescription("Remote")]
        public ActionResult Remote()
        {
            return JsonMore(LabText.Items());
        }

        [AllRights]
        [ActionDescription("RemoteTree")]
        public ActionResult RemoteTree()
        {
            return JsonMore(LabText.Tree());
        }

        [AllRights]
        [HttpPost]
        [ActionDescription("ChartData")]
        public ActionResult ChartData()
        {
            var data = new List<ChartData>
            {
                new ChartData { Category = "Mon", Series = "North", Value = 5 },
                new ChartData { Category = "Mon", Series = "South", Value = 7 },
                new ChartData { Category = "Tue", Series = "North", Value = 9 },
                new ChartData { Category = "Tue", Series = "South", Value = 3 },
                new ChartData { Category = LabText.SpecialItem, Series = "North", Value = 4 },
                new ChartData { Category = LabText.SpecialItem, Series = "South", Value = 6 },
            };
            return Json(data.ToChartData());
        }

        [AllRights]
        [HttpPost]
        [ActionDescription("ScatterData")]
        public ActionResult ScatterData()
        {
            var data = new List<ChartData>
            {
                new ChartData { Category = "p1", Series = "Group A", ValueX = 1, Value = 2, Addition = 5 },
                new ChartData { Category = "p2", Series = "Group A", ValueX = 2, Value = 4, Addition = 8 },
                new ChartData { Category = "p3", Series = LabText.SpecialItem, ValueX = 3, Value = 1, Addition = 3 },
            };
            return Json(data.ToChartData());
        }

        // ---- small helpers for the button fixtures -----------------------------------------------------------------

        [AllRights]
        [ActionDescription("Popup")]
        public ActionResult Popup()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            return PartialView(vm);
        }

        /// <summary>Answers a form that was posted into a dialog (ff.GetPostData) with the title it received.</summary>
        [AllRights]
        [ActionDescription("Echo")]
        public ActionResult Echo()
        {
            // the raw posted value, not the view model value (the framework's string binder HTML-encodes bound strings)
            var Title = Request.HasFormContentType ? Request.Form["Title"].ToString() : "";
            return Content("<div id=\"lab-echo\" style=\"padding:10px\">Received: <span id=\"lab-echo-title\"></span></div><script>$('#lab-echo-title').text(" + System.Text.Json.JsonSerializer.Serialize(Title ?? "") + ");</script>", "text/html");
        }

        [AllRights]
        [ActionDescription("Notify")]
        public ActionResult Notify()
        {
            return FFResult().Message("hello message");
        }

        /// <summary>A page that renders the UEditor tag helper (the editor scripts are not part of the shared static files, so tests only read the markup).</summary>
        [AllRights]
        [ActionDescription("Ueditor")]
        public ActionResult Ueditor()
        {
            var vm = Wtm.CreateVM<LabFormVM>();
            vm.Rich = LabText.Special;
            return PartialView(vm);
        }

        /// <summary>
        /// Test switch for UIOptions:EncodeHtmlContent (the host starts with it on). It changes the live configuration object, so it
        /// is meant for one test at a time; the tests that use it restore the value when they end.
        /// </summary>
        [AllRights]
        [ActionDescription("HtmlEncoding")]
        public ActionResult HtmlEncoding(bool on)
        {
            ConfigInfo.UIOptions.EncodeHtmlContent = on;
            return Content(ConfigInfo.UIOptions.EncodeHtmlContent.ToString().ToLowerInvariant(), "text/plain");
        }

        /// <summary>
        /// Test helper: the reference a page of an application would write for a view model when it calls the generic data endpoints from
        /// its own script (Wtm.CreateGenericEndpointReference). Fixture pages are open to every signed in user, so is this.
        /// </summary>
        [AllRights]
        [ActionDescription("GenericReference")]
        public ActionResult GenericReference(string vm, string purposes, string fields, string cs)
        {
            // cs: pins the reference to a connection key (empty: the connection the request itself runs on)
            var reference = Wtm.CreateGenericEndpointReference(Type.GetType(vm), Enum.Parse<GenericEndpointPurpose>(purposes), string.IsNullOrEmpty(cs) ? null : cs, string.IsNullOrEmpty(fields) ? null : fields.Split(','));
            return Content(reference ?? "", "text/plain");
        }

        [AllRights]
        [ActionDescription("Ping")]
        public ActionResult Ping()
        {
            return FFResult().Alert("pong");
        }

        /// <summary>Always fails, to exercise the framework's error handling page.</summary>
        [AllRights]
        [ActionDescription("Fail")]
        public ActionResult Fail()
        {
            throw new InvalidOperationException("Lab: simulated failure");
        }

        // jQuery posts a list as name[]=a&name[]=b; keep the plain name so that tests read the values by property name
        private void Record(string page)
        {
            LastPosts[page] = Request.Form.Where(x => x.Key != "FromView")
                .GroupBy(x => x.Key.EndsWith("[]") ? x.Key.Substring(0, x.Key.Length - 2) : x.Key)
                .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Value.ToArray()).ToArray());
        }

        /// <summary>Fails with the given text as the message.</summary>
        [AllRights]
        [ActionDescription("FailWith")]
        public ActionResult FailWith(string text)
        {
            throw new InvalidOperationException(text);
        }

        private static void Seed(LabFormVM vm)
        {
            vm.Title = LabText.Special;
            vm.Memo = LabText.Special;
            vm.Rich = "<p>Rich <b>bold</b></p>";
            vm.Keep = LabText.Special;
            vm.Shown = LabText.Special;
            vm.ShownAmount = 1234.5m;
            vm.Color = "#1e9fff";
            vm.Choice = LabText.SpecialItem;
            vm.Flags = new List<string> { LabText.SpecialItem };
            vm.MultiCombo = new List<string> { LabText.SpecialItem };
            vm.Picked = new List<string> { "p2" };
            vm.Branch = "leaf1b";
            vm.Branches = new List<string> { "leaf1a" };
        }
    }
}
