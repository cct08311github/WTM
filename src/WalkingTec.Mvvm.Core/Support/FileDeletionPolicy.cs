using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>The answer of an <see cref="IFileDeletionPolicy"/>.</summary>
    public enum FileDeletionDecision
    {
        /// <summary>No opinion: the framework rule applies (a file is deleted when the saved record refers to it).</summary>
        Default = 0,

        /// <summary>Delete the file even if the framework rule would skip it.</summary>
        Allow = 1,

        /// <summary>Do not delete the file.</summary>
        Deny = 2
    }

    /// <summary>Describes one file that a form asked to remove.</summary>
    public sealed class FileDeletionRequest
    {
        /// <summary>The file. <see cref="Guid.Empty"/> when the id sent by the client is not an id of this store (see <see cref="RawFileId"/>).</summary>
        public Guid FileId { get; set; }

        /// <summary>The id as the client sent it; set only when it is not a <see cref="Guid"/> (then <see cref="FileId"/> is empty).</summary>
        public string RawFileId { get; set; }

        /// <summary>True when the stored record being saved refers to the file (checked before the save).</summary>
        public bool IsLinked { get; set; }

        /// <summary>The operation: <c>Add</c>, <c>Edit</c> or <c>Import</c> (a form that saves a record), or <c>Endpoint</c> (the HTTP delete endpoint of the file API).</summary>
        public string Operation { get; set; }

        /// <summary>The signed-in user (null when nobody is signed in).</summary>
        public string User { get; set; }

        /// <summary>The tenant the user acts in.</summary>
        public string Tenant { get; set; }

        /// <summary>The context of the request.</summary>
        public WTMContext Wtm { get; set; }
    }

    /// <summary>
    /// Optional hook for the files that a form asks to remove when a record is saved (<c>BaseVM.DeletedFileIds</c>). By default a file is deleted
    /// only when the stored record that is being saved refers to it. An application registers its own implementation (before the framework
    /// services are added; the framework registers its default with TryAdd) to delete more, for example for administrators, or to delete less.
    /// An exception thrown by the policy counts as <see cref="FileDeletionDecision.Deny"/>.
    /// </summary>
    public interface IFileDeletionPolicy
    {
        FileDeletionDecision Decide(FileDeletionRequest request);
    }

    internal static class FileDeletionPolicyRunner
    {
        /// <summary>
        /// Asks the registered <see cref="IFileDeletionPolicy"/>. No policy: <see cref="FileDeletionDecision.Default"/>. A policy that throws:
        /// <see cref="FileDeletionDecision.Deny"/> (logged).
        /// </summary>
        public static FileDeletionDecision Ask(WTMContext wtm, FileDeletionRequest request, ILogger logger)
        {
            IFileDeletionPolicy policy = null;
            try
            {
                policy = wtm?.ServiceProvider?.GetService<IFileDeletionPolicy>();
            }
            catch (Exception)
            {
                policy = null;
            }
            if (policy == null)
            {
                return FileDeletionDecision.Default;
            }
            request.User = wtm.LoginUserInfo?.ITCode;
            request.Tenant = wtm.LoginUserInfo?.CurrentTenant;
            request.Wtm = wtm;
            try
            {
                return policy.Decide(request);
            }
            catch (Exception ex)
            {
                logger?.LogError("The file deletion policy failed: {ExceptionType}; the file {FileId} is kept", ex.GetType().Name, request.FileId == Guid.Empty ? LogText.Sanitize(request.RawFileId, 64) : request.FileId.ToString());
                return FileDeletionDecision.Deny;
            }
        }
    }

    /// <summary>The default policy: no opinion.</summary>
    public sealed class DefaultFileDeletionPolicy : IFileDeletionPolicy
    {
        public FileDeletionDecision Decide(FileDeletionRequest request) => FileDeletionDecision.Default;
    }
}
