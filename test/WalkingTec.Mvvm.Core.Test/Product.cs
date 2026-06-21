#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Test-only entity with an ISubFile navigation collection.
    /// Used by DoRealDeleteAsync_UnloadedSubFile regression tests.
    /// </summary>
    [Table("zz_product")]
    public class Product : TopBasePoco
    {
        [Required]
        [StringLength(100)]
        public string? Name { get; set; }

        public List<ProductAttachment>? Attachments { get; set; }
    }
}
