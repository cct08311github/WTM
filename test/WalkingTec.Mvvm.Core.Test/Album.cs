using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>A model with files: two single attachments and a list of sub files (what <c>BaseCRUDVM.FileRefs</c> is built from).</summary>
    public class Album : TopBasePoco
    {
        [Display(Name = "Title")]
        public string Title { get; set; }

        public Guid? CoverId { get; set; }
        public FileAttachment Cover { get; set; }

        public Guid? ScanId { get; set; }
        public FileAttachment Scan { get; set; }

        public List<AlbumPhoto> Photos { get; set; }
    }

    public class AlbumPhoto : TopBasePoco, ISubFile
    {
        public Guid AlbumId { get; set; }
        public Album Album { get; set; }

        public Guid FileId { get; set; }
        public FileAttachment File { get; set; }
        public int Order { get; set; }
    }
}
