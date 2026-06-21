#nullable enable
using System;
using System.ComponentModel.DataAnnotations.Schema;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Test-only sub-file join entity for ProductWithFiles.
    /// Implements ISubFile so DoRealDelete(Async) handles it as an attachment collection.
    /// </summary>
    [Table("zz_product_attachment")]
    public class ProductAttachment : TopBasePoco, ISubFile
    {
        public Guid ProductId { get; set; }
        public Product? Product { get; set; }

        public Guid FileId { get; set; }
        public FileAttachment? File { get; set; }

        public int Order { get; set; }
    }
}
