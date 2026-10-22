using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;
using WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Controllers
{
    /// <summary>
    /// Test only: pages whose actions have a fixed connection other than the default. The connection <c>other</c> has a read replica
    /// <c>other_1</c> (a database of its own), so a read runs on the replica and a write on the base connection. Open to every
    /// signed-in user, like the Lab fixture pages.
    /// </summary>
    [AllRights]
    [ActionDescription("OtherDb")]
    public class OtherDbController : BaseController
    {
        [FixConnection(DBOperationEnum.Default, "other")]
        [ActionDescription("Grid")]
        public ActionResult Grid()
        {
            return PartialView(Wtm.CreateVM<GadgetListVM>());
        }

        [FixConnection(DBOperationEnum.Default, "other")]
        [ActionDescription("Import")]
        public ActionResult Import()
        {
            return PartialView(Wtm.CreateVM<GadgetImportVM>());
        }

        /// <summary>A reference issued on the connection of this action (the replica: it is a read).</summary>
        [FixConnection(DBOperationEnum.Default, "other")]
        [ActionDescription("Reference")]
        public ActionResult Reference(string vm, string purposes, string fields)
        {
            var reference = Wtm.CreateGenericEndpointReference(Type.GetType(vm), Enum.Parse<GenericEndpointPurpose>(purposes), null, string.IsNullOrEmpty(fields) ? null : fields.Split(','));
            return Content(reference ?? "", "text/plain");
        }

        /// <summary>The rows of the base connection, "id|name" per line.</summary>
        [FixConnection(DBOperationEnum.Write, "other")]
        [ActionDescription("BaseGadgets")]
        public ActionResult BaseGadgets()
        {
            return Content(string.Join("\n", DC.Set<Gadget>().OrderBy(x => x.Name).ToList().Select(x => x.ID + "|" + x.Name)), "text/plain");
        }

        /// <summary>The rows of the read replica, "id|name" per line.</summary>
        [FixConnection(DBOperationEnum.Read, "other")]
        [ActionDescription("ReplicaGadgets")]
        public ActionResult ReplicaGadgets()
        {
            return Content(string.Join("\n", DC.Set<Gadget>().OrderBy(x => x.Name).ToList().Select(x => x.ID + "|" + x.Name)), "text/plain");
        }
    }
}
