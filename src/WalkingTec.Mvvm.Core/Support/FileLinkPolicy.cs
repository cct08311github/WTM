using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>The answer of an <see cref="IFileLinkPolicy"/>.</summary>
    public enum FileLinkDecision
    {
        /// <summary>No opinion: the framework rule applies (the file needs a valid <c>LinkRef</c> of the signed-in user).</summary>
        Default = 0,

        /// <summary>Link the file even without a reference (for example a file that server code attached on purpose).</summary>
        Allow = 1,

        /// <summary>Do not link the file, even if a valid reference was sent.</summary>
        Deny = 2
    }

    /// <summary>Describes one file that a form newly links to the record it saves.</summary>
    public sealed class FileLinkRequest
    {
        /// <summary>The file.</summary>
        public Guid FileId { get; set; }

        /// <summary>The property of the record that carries the file (the foreign key of a single attachment, or the <c>ISubFile</c> list).</summary>
        public string Property { get; set; }

        /// <summary>The operation: <c>Add</c> or <c>Edit</c>.</summary>
        public string Operation { get; set; }

        /// <summary>True when the request carries a valid <c>LinkRef</c> for this file, issued to the signed-in user.</summary>
        public bool HasValidReference { get; set; }

        /// <summary>The signed-in user (null when nobody is signed in).</summary>
        public string User { get; set; }

        /// <summary>The tenant the user acts in.</summary>
        public string Tenant { get; set; }

        /// <summary>The context of the request.</summary>
        public WTMContext Wtm { get; set; }
    }

    /// <summary>
    /// Optional hook for the files that a form newly links to a record (<c>BaseCRUDVM.DoAdd</c>, <c>DoEdit</c> and their async versions). By default
    /// a file that the stored record did not already refer to must be covered by the <c>LinkRef</c> that the upload endpoints gave to the signed-in user.
    /// An application registers its own implementation (before the framework services are added; the framework registers its default with TryAdd) to
    /// admit more (<see cref="FileLinkDecision.Allow"/>, for example server-side links or administrators) or to refuse more (<see cref="FileLinkDecision.Deny"/>).
    /// It is asked for each newly linked file. An exception thrown by the policy counts as <see cref="FileLinkDecision.Deny"/>.
    /// </summary>
    public interface IFileLinkPolicy
    {
        FileLinkDecision Decide(FileLinkRequest request);
    }

    internal static class FileLinkPolicyRunner
    {
        /// <summary>
        /// Asks the registered <see cref="IFileLinkPolicy"/>. No policy: <see cref="FileLinkDecision.Default"/>. A policy that throws:
        /// <see cref="FileLinkDecision.Deny"/> (logged).
        /// </summary>
        public static FileLinkDecision Ask(WTMContext wtm, FileLinkRequest request, ILogger logger)
        {
            IFileLinkPolicy policy = null;
            try
            {
                policy = wtm?.ServiceProvider?.GetService<IFileLinkPolicy>();
            }
            catch (Exception)
            {
                policy = null;
            }
            if (policy == null)
            {
                return FileLinkDecision.Default;
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
                logger?.LogError("The file link policy failed: {ExceptionType}; the file {FileId} is not linked", ex.GetType().Name, request.FileId);
                return FileLinkDecision.Deny;
            }
        }
    }

    /// <summary>The default policy: no opinion.</summary>
    public sealed class DefaultFileLinkPolicy : IFileLinkPolicy
    {
        public FileLinkDecision Decide(FileLinkRequest request) => FileLinkDecision.Default;
    }

    internal static class FileLinkChecker
    {
        /// <summary>The most link references that are opened for one request (a form carries a few; this bounds the work for an unusually long one).</summary>
        private const int MaxReferences = 500;

        /// <summary>
        /// The files among <paramref name="fresh"/> (newly linked, not yet stored with the record) that may not be linked: for each one the registered
        /// <see cref="IFileLinkPolicy"/> is asked; without an opinion the file needs a valid link reference of the signed-in user in
        /// <paramref name="references"/>, unless the proof is skipped (<see cref="WTMContext.FileLinkProofSkipped"/>). Every refusal is logged.
        /// </summary>
        public static List<(Guid Id, string Property)> Refused(WTMContext wtm, IEnumerable<(Guid Id, string Property)> fresh, IEnumerable<string> references, string operation, ILogger logger)
        {
            var refused = new List<(Guid Id, string Property)>();
            var proven = new HashSet<Guid>();
            foreach (var reference in (references ?? Enumerable.Empty<string>()).Where(x => string.IsNullOrEmpty(x) == false).Take(MaxReferences))
            {
                var id = wtm.ReadFileLinkReference(reference);
                if (id != null)
                {
                    proven.Add(id.Value);
                }
            }
            var skipped = wtm.FileLinkProofSkipped;
            foreach (var link in fresh)
            {
                var hasReference = proven.Contains(link.Id);
                var decision = FileLinkPolicyRunner.Ask(wtm, new FileLinkRequest { FileId = link.Id, Property = link.Property, Operation = operation, HasValidReference = hasReference }, logger);
                var allowed = decision == FileLinkDecision.Deny ? false : decision == FileLinkDecision.Allow ? true : (skipped || hasReference);
                if (allowed == false)
                {
                    refused.Add(link);
                    logger?.LogWarning("A {Operation} was refused: the file {FileId} in {Property} is not covered by a link reference of the user (decision {Decision}, user {User})",
                        operation, link.Id, LogText.Sanitize(link.Property, 100), decision, LogText.Sanitize(wtm.LoginUserInfo?.ITCode, 100));
                }
            }
            return refused;
        }
    }
}
