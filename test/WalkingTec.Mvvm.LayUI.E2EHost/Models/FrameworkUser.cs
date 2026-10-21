using System.ComponentModel.DataAnnotations.Schema;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// The framework looks for a type named FrameworkUser derived from FrameworkUserBase as the login user model.
    /// </summary>
    [Table("FrameworkUsers")]
    public class FrameworkUser : FrameworkUserBase
    {
    }
}
