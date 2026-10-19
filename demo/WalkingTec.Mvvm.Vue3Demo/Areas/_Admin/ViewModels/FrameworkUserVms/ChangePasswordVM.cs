// WTM默认页面 Wtm buidin page
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkUserVms
{
    public class ChangePasswordVM : BaseVM
    {
        [Display(Name = "_Admin.Account")]
        public string ITCode { get; set; }

        [Display(Name = "Login.OldPassword")]
        [Required(AllowEmptyStrings = false)]
        [StringLength(50, ErrorMessage = "Validate.{0}stringmax{1}")]
        public string OldPassword { get; set; }

        [Display(Name = "Login.NewPassword")]
        [Required(AllowEmptyStrings = false)]
        [StringLength(50, ErrorMessage = "Validate.{0}stringmax{1}")]
        public string NewPassword { get; set; }

        [Display(Name = "Login.NewPasswordComfirm")]
        [Required(AllowEmptyStrings = false)]
        [StringLength(50, ErrorMessage = "Validate.{0}stringmax{1}")]
        public string NewPasswordComfirm { get; set; }

        public override void Validate()
        {
            List<ValidationResult> rv = new List<ValidationResult>();
            //several rows can share an account name (for example under a case insensitive collation), so check every candidate
            var storedPasswords = DC.Set<FrameworkUser>().Where(x => x.ITCode == LoginUserInfo.ITCode).Select(x => x.Password).ToList();
            if (storedPasswords.Any(x => Wtm.VerifyPassword(x, OldPassword) != PasswordVerifyResult.Failed) == false)
            {
                MSD.AddModelError("OldPassword", Localizer["Login.OldPasswrodWrong"]);
            }
            if (NewPassword != NewPasswordComfirm)
            {
                MSD.AddModelError("NewPasswordComfirm", Localizer["Login.PasswordNotSame"]);
            }
        }

        public void DoChange()
        {
            var user = DC.Set<FrameworkUser>().Where(x => x.ITCode == LoginUserInfo.ITCode).SingleOrDefault();
            if (user != null)
            {
                user.Password = Wtm.HashPassword(NewPassword);
            }
            DC.SaveChanges();
        }
    }
}
