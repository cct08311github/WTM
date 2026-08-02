#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Exceptions;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Issue #824: the single SaveChanges-level decision that every EF-tracked write through an
    /// <see cref="EmptyContext"/>-derived context passes through — not a per-sink gate. Five
    /// rounds of per-sink gates (<c>BaseCRUDVM.DoAddPrepare</c>/<c>DoEditPrepare</c>,
    /// <c>_FrameworkController.UpdateModelProperty</c>, ...) each found a NEW code path that
    /// structurally bypassed the previous ones and wrote a <see cref="FileAttachment"/>-typed
    /// foreign key the caller could not legitimately reference. This guard is the backstop: it
    /// runs inside <see cref="EmptyContext"/>'s four <c>SaveChanges</c>/<c>SaveChangesAsync</c>
    /// overrides, before <c>ApplyAuditFields</c>, and inspects <c>ChangeTracker.Entries()</c>
    /// directly — so it sees every tracked Added/Modified entity regardless of which VM,
    /// controller action, batch operation, import mapping, or direct <c>DbSet</c> call put it
    /// there.
    /// <para>
    /// <b>Narrowing (state honestly, per Issue #824's Red Line):</b> this covers every
    /// EF-tracked write that flows through an <see cref="EmptyContext"/>-derived
    /// <see cref="DbContext"/>'s own <c>SaveChanges</c>/<c>SaveChangesAsync</c> — NOT every write
    /// path in the framework. <see cref="IDataContext"/> is a public interface
    /// (<c>IDataContext.cs</c> documents external implementers); <c>CS.Cis</c>/<c>CS.CisFull</c>
    /// scan for ANY <see cref="DbContext"/> constructor, not only <see cref="EmptyContext"/>
    /// subclasses; and <c>WalkingTec.Mvvm.WorkFlow.ServiceCollectionExtensions.ResolveDataContext</c>'s
    /// fallback branch returns whatever <see cref="IDataContext"/> is registered in DI, which is
    /// not guaranteed to derive from <see cref="EmptyContext"/> either. A host that registers an
    /// external, non-<see cref="EmptyContext"/> <see cref="IDataContext"/> implementation does
    /// NOT get this guard for free — see the CHANGELOG's #824 migration note.
    /// </para>
    /// <para>
    /// <b>Resolved against <c>FileAttachment.ID</c> only (Issue #824 adversarial review Finding 7;
    /// Issue #985 hardened this from a disclosed gap into an enforced model requirement):</b>
    /// <see cref="DCExtension.ResolveFileAttachmentIds"/>/<see cref="DCExtension.ResolveFileAttachmentIdsAsync"/>
    /// hardcode the query <c>x.ID</c> — so "this FK's relationship SHAPE matches" (principal is
    /// <see cref="FileAttachment"/> or derived) is not the same fact as "the resolution query
    /// answers the right question for THIS FK". <c>BuildMap</c> therefore requires the
    /// relationship's <c>PrincipalKey</c> to be exactly the single <see cref="Guid"/>
    /// <c>TopBasePoco.ID</c> primary key before trusting a Guid-typed candidate to that query.
    /// Two other legal EF Core shapes exist, both configured via
    /// <c>HasForeignKey(...).HasPrincipalKey(x =&gt; x.SomeAlternateKey)</c>:
    /// <list type="bullet">
    /// <item>a non-Guid alternate key (e.g. a <see cref="string"/> column) can never produce a
    /// Guid candidate at all — recognised by shape, excluded from the map, and disclosed via a
    /// throttled <c>LogWarning</c> the first time such a field is seen
    /// (<see cref="LogNonGuidAttachmentFk"/>), unchanged from Finding 7's original behaviour;</item>
    /// <item>a Guid-typed alternate key (single, or a composite key with at least one Guid
    /// component) would otherwise silently resolve its candidates against <c>x.ID</c> instead of
    /// the actual principal key — producing BOTH a false allow (an attacker whose own row's ID
    /// happens to equal the victim's alternate-key value is waved through) and a false reject (a
    /// legitimate alternate-key value is never found by ID). <c>BuildMap</c> now throws
    /// <see cref="NotSupportedException"/> the first time such a model is used, rather than
    /// silently mis-resolving it — a model-configuration error, not a per-request rejection. See
    /// the CHANGELOG's #985 entry for the full false-allow/false-reject analysis and remediation.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Never rewrite a posted value.</b> Earlier rounds on this issue cleared an unauthorized
    /// FK to <see cref="Guid.Empty"/>; the column is typically non-nullable with a real FK
    /// constraint, so that write matched no row and turned a security fix into a silent rollback
    /// / unhandled <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>. This guard
    /// REJECTS the transaction instead — see <see cref="UnresolvableFileAttachmentReferenceException"/>.
    /// </para>
    /// <para>
    /// <b>Invariant a downstream <c>SaveChanges</c> hook must preserve (cross-vendor review
    /// Finding 2):</b> this guard runs BEFORE <c>base.SaveChanges()</c>/<c>SaveChangesAsync()</c>
    /// is ever entered, so it sees each entry's state as the caller left it. EF Core's own
    /// <c>SavingChanges</c> interceptor pipeline runs LATER, inside <c>base.SaveChanges</c>, and
    /// an interceptor is free to mutate the change tracker before the actual database command is
    /// built — including flipping a <see cref="FileAttachment"/> entry this guard just trusted as
    /// <see cref="EntityState.Added"/> (the "same unit of work" exception below) back to
    /// <see cref="EntityState.Unchanged"/>, which skips the INSERT and therefore the PK
    /// constraint that exception relies on, or setting a dependent's FK scalar after this guard
    /// already ran. <b>A <c>SaveChanges</c>/<c>SavingChanges</c> hook registered on an
    /// <see cref="EmptyContext"/>-derived context must never mutate a <see cref="FileAttachment"/>
    /// entry's state or a FileAttachment-typed FK scalar after this guard runs.</b> No production
    /// interceptor in this repository does this today — this is a documented invariant for
    /// anyone adding one, not a claim that every possible interceptor is safe.
    /// </para>
    /// </summary>
    public static class FileAttachmentSaveChangesGuard
    {
        /// <summary>
        /// Global kill switch. Default <see langword="true"/> (ON) — Security-over-Compatibility
        /// was settled for this issue family in #815. Setting this to <see langword="false"/>
        /// reopens #824: any authenticated caller can once again write a
        /// <see cref="FileAttachment"/>-typed foreign key pointing at another tenant's row,
        /// because nothing after the per-sink gates (which #824's own history shows are
        /// structurally incomplete) will stop it. This is a plain static switch, not a
        /// DI-registered option, because <see cref="EmptyContext"/>-derived contexts are commonly
        /// constructed by reflection (<c>CS.CreateDC()</c>) with no DI container involved — see
        /// this class's own doc comment and the CHANGELOG's #824 entry for why an
        /// <c>IOptions&lt;T&gt;</c>-based switch would not reach every context that needs it.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        // Issue #824: contexts are commonly per-request, reflection-constructed instances (see
        // CS.CreateDC()), but EF Core caches/reuses the SAME IModel instance across every context
        // instance of a given concrete DbContext type + configuration. Keying this cache on the
        // IModel reference (default reference-equality Dictionary/ConcurrentDictionary behaviour
        // for IModel, which does not override Equals/GetHashCode) means the FK map is built once
        // PER DISTINCT MODEL, not once per request. An instance-level cache on EmptyContext itself
        // would be useless — a fresh instance never reuses another instance's cache.
        private static readonly ConcurrentDictionary<IModel, IReadOnlyDictionary<IEntityType, FkAttachmentInfo[]>> _fkMapCache = new();

        /// <summary>
        /// Issue #824 adversarial review Finding 6 (PR #978): a candidate FK's own declared
        /// <see cref="PrincipalClrType"/> — kept alongside the property name so the "same unit of
        /// work" trust check can require the SPECIFIC type that was Added, not just any
        /// <see cref="FileAttachment"/>-derived type sharing that id. See <see cref="CollectCandidates"/>'s
        /// doc comment for why this matters specifically for TPC (Table-Per-Concrete-Type)
        /// mapping.
        /// </summary>
        private readonly record struct FkAttachmentInfo(string PropertyName, Type PrincipalClrType);

        // Issue #824 adversarial review Finding 3 (PR #978, MUST FIX): a rejection here produced
        // NO server-side signal at all. BaseBatchVM.DoBatchEdit/DoBatchDelete's own catch calls
        // SetExceptionMessage(e, id: null), whose body is `if (id != null) {...}` — with a null id
        // the message is discarded entirely (BaseBatchVM.cs), so a live cross-tenant forgery
        // through batch edit, or a resolution-query outage rejecting every save in a tenant, left
        // literally nothing an operator or a security-monitoring pipeline could find. Per this
        // repo's own security rule ("安全事件要留 log"), and matching the precedent this codebase
        // already established for a DI-less static class logging a fail-closed security decision
        // (DCExtension.ApplyDataPrivilegeForAnalysis's #843 throttled LogWarning via
        // CoreProgram.GetLogger), every rejection now logs — throttled per (entity type, property)
        // so a sustained outage or a repeated attack against the same field cannot flood the log,
        // while the FIRST occurrence (the one that matters for triage) is always captured.
        private static readonly ConcurrentDictionary<string, byte> _loggedRejections = new();
        private static readonly ConcurrentDictionary<string, byte> _loggedResolutionFailures = new();

        // Issue #824 adversarial review Finding 7 (PR #978): BuildMap recognises an FK by its
        // relationship SHAPE (principal is FileAttachment or derived) regardless of the FK
        // property's own CLR type, but every candidate-collection/resolution path downstream
        // (ExtractGuid, ResolveFileAttachmentIds) works in Guid only. Every FileAttachment-derived
        // type shipped in this repository inherits TopBasePoco.ID (Guid, non-virtual), so a
        // convention-discovered FK following the PRINCIPAL KEY is always Guid-typed — this throttle
        // exists for the one legal EF Core shape that is NOT: a downstream HasForeignKey(...)
        // .HasPrincipalKey(x => x.SomeAlternateKey) against a non-Guid alternate key on a
        // FileAttachment-derived type. See LogNonGuidAttachmentFk's doc comment.
        private static readonly ConcurrentDictionary<string, byte> _loggedNonGuidFks = new();

        // Issue #1000 Part 1: BuildMap's fourth decision -- the #985 NotSupportedException thrown
        // when a FileAttachment-principal FK's principal key is neither the canonical single Guid
        // ID nor entirely non-Guid -- shipped with #985 emitting no log at all, unlike the other
        // three decisions above. Same rationale as Finding 3's comment on _loggedRejections: a
        // rejection with no server-side signal leaves an operator or a security-monitoring
        // pipeline nothing to find. Throttled the same way, keyed on (entity type, FK
        // propert(y/ies)), because _fkMapCache's own doc comment already establishes that
        // ConcurrentDictionary.GetOrAdd does not cache a throwing factory -- BuildMap, and
        // therefore this decision, reruns on EVERY SaveChanges against the same misconfigured
        // model, not merely the first.
        private static readonly ConcurrentDictionary<string, byte> _loggedPrincipalKeyRejections = new();

        /// <summary>
        /// Test/diagnostic seam: clears the static per-model cache. Production code never needs
        /// this — the cache is keyed on <see cref="IModel"/> reference identity and a given model
        /// never changes shape at runtime — but a test suite that builds many short-lived, subtly
        /// different in-memory model shapes across a long-running test process benefits from not
        /// growing this cache unboundedly.
        /// </summary>
        internal static void ClearCacheForTests() => _fkMapCache.Clear();

        /// <summary>
        /// Test seam: clears the two logging throttle sets above, so a test suite that
        /// deliberately triggers the SAME (entity type, property) rejection more than once across
        /// different test methods can still observe a fresh log line each time it needs to.
        /// </summary>
        internal static void ClearLoggingThrottleForTests()
        {
            _loggedRejections.Clear();
            _loggedResolutionFailures.Clear();
            _loggedNonGuidFks.Clear();
            _loggedPrincipalKeyRejections.Clear();
        }

        private static void LogRejection(Candidate candidate)
        {
            var key = $"{candidate.EntityClrType.FullName}.{candidate.PropertyName}";
            if (_loggedRejections.TryAdd(key, 0))
            {
                CoreProgram.GetLogger("FileAttachmentSaveChangesGuard")?.LogWarning(
                    "Issue #824: rejected an unresolvable FileAttachment reference on " +
                    "{EntityType}.{PropertyName} (posted id {Id}) — the id does not resolve to a " +
                    "FileAttachment row under the caller's own tenant scope. This is either a " +
                    "forged cross-tenant reference or a pre-existing row this guard now correctly " +
                    "rejects on edit (see the CHANGELOG's #824 migration note and its data-audit " +
                    "query). Logged once per (entity type, property) per process — further " +
                    "occurrences on this same field are still rejected but not logged again.",
                    candidate.EntityClrType.FullName, candidate.PropertyName, candidate.Id);
            }
        }

        private static void LogResolutionFailure(Exception? failure)
        {
            if (_loggedResolutionFailures.TryAdd("resolution-query-failure", 0))
            {
                CoreProgram.GetLogger("FileAttachmentSaveChangesGuard")?.LogWarning(failure,
                    "Issue #824: the batched FileAttachment resolution query itself failed; " +
                    "rejecting the whole save rather than narrowing (a query failure is not the " +
                    "same fact as \"these ids don't exist\" — see Issue #828's precedent for the " +
                    "same distinction at the VM layer). Logged once per process — further " +
                    "occurrences during the same outage are still rejected but not logged again.");
            }
        }

        /// <summary>
        /// Issue #824 adversarial review Finding 7 (PR #978, disclosed via logging rather than
        /// enforced — see this class's own doc comment on scope): <paramref name="entityType"/>.
        /// <paramref name="propertyName"/> is a foreign key whose relationship PRINCIPAL is
        /// <see cref="FileAttachment"/> or a type derived from it (the shape <see cref="BuildMap"/>
        /// looks for), but its own CLR type is not <see cref="Guid"/>/<c>Guid?</c> — every
        /// candidate-collection and resolution path this guard runs (<see cref="ExtractGuid"/>,
        /// <see cref="DCExtension.ResolveFileAttachmentIds"/>) works in <see cref="Guid"/> only, so
        /// this field is recognised but NEVER added to the map, NEVER produces a candidate, and is
        /// therefore not covered by this guard's protection at all — silently, before this fix.
        /// Full support for an arbitrary alternate-key type is out of scope for a boundary guard
        /// whose whole design is "one batched Guid-keyed resolution query"; this makes the gap
        /// LOUD instead, once per (entity type, property) per process, so an operator or a future
        /// maintainer extending the model this way finds out from the log rather than from a
        /// cross-tenant write nothing rejected.
        /// </summary>
        private static void LogNonGuidAttachmentFk(IEntityType entityType, string propertyName, Type principalClrType, Type propertyClrType)
        {
            var key = $"{entityType.ClrType.FullName}.{propertyName}";
            if (_loggedNonGuidFks.TryAdd(key, 0))
            {
                CoreProgram.GetLogger("FileAttachmentSaveChangesGuard")?.LogWarning(
                    "Issue #824: {EntityType}.{PropertyName} is a foreign key whose relationship " +
                    "principal is {PrincipalType} (a FileAttachment or a type derived from it), " +
                    "but its own CLR type is {PropertyType}, not Guid/Guid? — this guard resolves " +
                    "FileAttachment references by Guid id only and CANNOT enforce this field. It " +
                    "is recognised but NOT covered by the #824 SaveChanges boundary guard — see " +
                    "the CHANGELOG's #824 migration note. Logged once per (entity type, property) " +
                    "per process.",
                    entityType.ClrType.FullName, propertyName, principalClrType.FullName, propertyClrType.FullName);
            }
        }

        /// <summary>
        /// Issue #1000 Part 1: logs the same fact <see cref="BuildPrincipalKeyRejectionException"/>
        /// throws for -- a FileAttachment-principal FK whose principal key is neither the
        /// canonical single Guid <c>ID</c> nor entirely non-Guid -- at the decision point itself,
        /// matching the shape, level, and once-per-process-per-field throttle discipline this
        /// class already established for its other three decisions (<see cref="LogRejection"/>,
        /// <see cref="LogResolutionFailure"/>, <see cref="LogNonGuidAttachmentFk"/>; see the
        /// rationale on <see cref="_loggedRejections"/> above). Called immediately before the
        /// throw in <see cref="BuildMap"/>; never called anywhere else.
        /// </summary>
        private static void LogPrincipalKeyRejection(IEntityType entityType, IForeignKey fk, Type principalClrType, IReadOnlyKey principalKey)
        {
            var fkPropertyNames = string.Join(", ", fk.Properties.Select(p => p.Name));
            var key = $"{entityType.ClrType.FullName}.{fkPropertyNames}";
            if (_loggedPrincipalKeyRejections.TryAdd(key, 0))
            {
                var principalKeyPropertyNames = string.Join(", ", principalKey.Properties.Select(p => p.Name));
                CoreProgram.GetLogger("FileAttachmentSaveChangesGuard")?.LogWarning(
                    "Issue #985: {EntityType}.{FkProperties} is a foreign key onto {PrincipalType} " +
                    "(a FileAttachment or a type derived from it) whose principal key is " +
                    "{PrincipalKeyProperties}, not the single Guid ID primary key — this model " +
                    "shape is not supported by the #824 SaveChanges boundary guard, and every " +
                    "SaveChanges call against this model will keep throwing NotSupportedException " +
                    "until it is fixed (see the CHANGELOG's #985 entry). Logged once per (entity " +
                    "type, FK propert(y/ies)) per process — further SaveChanges calls against the " +
                    "same field still throw but are not logged again.",
                    entityType.ClrType.FullName, fkPropertyNames, principalClrType.FullName, principalKeyPropertyNames);
            }
        }

        private static IReadOnlyDictionary<IEntityType, FkAttachmentInfo[]> GetOrBuildMap(IModel model)
        {
            return _fkMapCache.GetOrAdd(model, BuildMap);
        }

        /// <summary>
        /// Cheap-exit layer 1: walks every entity type in the model ONCE (per distinct model, via
        /// the cache above) and records, per <see cref="IEntityType"/>, every scalar FK property
        /// whose relationship's PRINCIPAL is <see cref="FileAttachment"/> or a class derived from
        /// it (Issue #824 Finding 1 — the same predicate as
        /// <see cref="DCExtension.IsFileAttachmentForeignKeyProperty(IDataContext?, Type?, string?)"/>,
        /// applied here directly against <see cref="IForeignKey"/> metadata since this walks the
        /// WHOLE model rather than one caller-supplied (type, property) pair at a time), ALONGSIDE
        /// that FK's own specific <see cref="FkAttachmentInfo.PrincipalClrType"/> (Issue #824
        /// Finding 6 — needed so <see cref="CollectCandidates"/> can require the SAME-unit-of-work
        /// exception to match the SPECIFIC derived type a relationship declares, not merely "any
        /// FileAttachment-derived type shares this id"). A model with no such relationship
        /// anywhere returns an empty map, and <see cref="Guard"/>/<see cref="GuardAsync"/> return
        /// immediately without ever calling <c>ChangeTracker.Entries()</c>.
        /// </summary>
        private static IReadOnlyDictionary<IEntityType, FkAttachmentInfo[]> BuildMap(IModel model)
        {
            var map = new Dictionary<IEntityType, FkAttachmentInfo[]>();
            foreach (var entityType in model.GetEntityTypes())
            {
                List<FkAttachmentInfo>? infos = null;
                foreach (var fk in entityType.GetForeignKeys())
                {
                    // Issue #824 Finding 1, cross-vendor review follow-up (PR #978): this used to
                    // do its own inline IsAssignableFrom check here — a second, independent copy
                    // of the exact comparison DCExtension.IsFileAttachmentForeignKeyProperty makes,
                    // which meant Finding 1's fix (landed in that method only) never actually
                    // reached this guard's own FK map, and the guard's protection against a
                    // derived-principal (TPH/TPT) attachment FK was unproven — the registered
                    // mutant only exercised the predicate in isolation, never this code path. Both
                    // now call the SAME shared comparison (DCExtension.IsFileAttachmentPrincipal),
                    // so a future regression in either shape is caught wherever it's tested.
                    var principalClrType = fk.PrincipalEntityType?.ClrType;
                    if (!DCExtension.IsFileAttachmentPrincipal(principalClrType))
                    {
                        continue;
                    }

                    // Issue #985 (cross-vendor review of #824, PR #978 follow-up): the shape
                    // check above says only "the principal is FileAttachment-or-derived" — it
                    // says nothing about WHICH key on that principal this relationship targets.
                    // DCExtension.ResolveFileAttachmentIds(Async)'s resolution query hardcodes
                    // `x.ID` (DCExtension.FileAttachmentResolution.cs:72,:114), so a relationship
                    // that targets any OTHER key resolves its candidates against the WRONG
                    // column. A design gate settled this as a model-configuration error to
                    // reject LOUDLY here, not a resolution query to generalize — see this class's
                    // own doc comment. This check deliberately runs BEFORE the Finding 7
                    // per-property loop below (which stays byte-for-byte unchanged) and
                    // only ever throws for the ONE shape that loop cannot already handle safely:
                    // a non-canonical principal key with at least one Guid-typed FK property. A
                    // non-canonical principal key with NO Guid-typed FK property falls straight
                    // through to that unchanged loop, which takes the existing Finding 7
                    // warn-and-exclude path for every one of its properties, exactly as before.
                    var principalKey = fk.PrincipalKey;
                    if (!IsCanonicalFileAttachmentPrincipalKey(principalKey)
                        && fk.Properties.Any(p => IsGuidTypedProperty(p.ClrType)))
                    {
                        // Issue #1000 Part 1: log at this decision point before throwing -- see
                        // LogPrincipalKeyRejection's own doc comment. Matches this class's
                        // established pattern of logging BEFORE throw at every other rejection
                        // site rather than leaving it to a caller's catch block.
                        LogPrincipalKeyRejection(entityType, fk, principalClrType!, principalKey);
                        throw BuildPrincipalKeyRejectionException(entityType, fk, principalClrType!, principalKey);
                    }

                    infos ??= [];
                    foreach (var property in fk.Properties)
                    {
                        if (infos.Exists(i => i.PropertyName == property.Name))
                        {
                            continue;
                        }
                        // Issue #824 Finding 7: the shape check above (principal is
                        // FileAttachment-derived) says nothing about the FK property's OWN CLR
                        // type — a relationship declared via HasPrincipalKey against a non-Guid
                        // alternate key produces exactly this shape with a non-Guid FK. Every
                        // downstream path (ExtractGuid, the resolution query) is Guid-only, so a
                        // non-Guid match is disclosed via LogNonGuidAttachmentFk and left OUT of
                        // the map rather than added — same observable behaviour as before this
                        // fix (still unenforced), now with a signal instead of silence.
                        var propertyClrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                        if (propertyClrType != typeof(Guid))
                        {
                            LogNonGuidAttachmentFk(entityType, property.Name, principalClrType!, property.ClrType);
                            continue;
                        }
                        infos.Add(new FkAttachmentInfo(property.Name, principalClrType!));
                    }
                }
                if (infos is { Count: > 0 })
                {
                    map[entityType] = [.. infos];
                }
            }
            return map;
        }

        /// <summary>
        /// Issue #985: true when <paramref name="clrType"/> — a candidate/nullable-unwrapped
        /// property CLR type — is <see cref="Guid"/>. Shared by the new principal-key check and
        /// (in spirit — kept as its own inline expression there for locality) the pre-existing
        /// Finding 7 per-property loop, both of which need the exact same nullable-unwrap-then-
        /// compare test.
        /// </summary>
        private static bool IsGuidTypedProperty(Type clrType)
        {
            return (Nullable.GetUnderlyingType(clrType) ?? clrType) == typeof(Guid);
        }

        /// <summary>
        /// Issue #985: true only when <paramref name="principalKey"/> IS the single <see cref="Guid"/>
        /// <c>TopBasePoco.ID</c> primary key that <see cref="DCExtension.ResolveFileAttachmentIds"/>
        /// and <see cref="DCExtension.ResolveFileAttachmentIdsAsync"/> hardcode (<c>x.ID</c>).
        /// <para>
        /// <see cref="IReadOnlyKey.IsPrimaryKey"/> ALONE is not the invariant this guard's
        /// resolution query relies on: a downstream context is free to re-declare
        /// <see cref="FileAttachment"/>'s primary key in its own <c>OnModelCreating</c> (a
        /// composite key, or a differently-named single column) — that would still pass
        /// <c>IsPrimaryKey()</c> while still not being the <c>ID</c> column the resolution query
        /// actually filters on. This requires ALL FOUR: it is the primary key, it is exactly one
        /// property, that property is named <c>"ID"</c>, and its (nullable-unwrapped) CLR type is
        /// <see cref="Guid"/>.
        /// </para>
        /// </summary>
        private static bool IsCanonicalFileAttachmentPrincipalKey(IReadOnlyKey principalKey)
        {
            return principalKey.IsPrimaryKey()
                && principalKey.Properties.Count == 1
                && principalKey.Properties[0].Name == nameof(TopBasePoco.ID)
                && IsGuidTypedProperty(principalKey.Properties[0].ClrType);
        }

        /// <summary>
        /// Issue #985: builds the <see cref="NotSupportedException"/> thrown from <see cref="BuildMap"/>
        /// when a FileAttachment-principal FK's principal key is neither the canonical single Guid
        /// <c>ID</c> primary key nor entirely non-Guid (the still-supported, still-disclosed
        /// Finding 7 shape). Deliberately a plain <see cref="NotSupportedException"/> rather than
        /// <see cref="UnresolvableFileAttachmentReferenceException"/> — that type means "a POSTED id
        /// failed tenant-scoped resolution at request time"; this is a MODEL CONFIGURATION error,
        /// discovered once per distinct <see cref="IModel"/> at first use, not a per-request
        /// security decision, and reusing the request-time type would make a configuration mistake
        /// look like a detected attack. The message names the entity, the FK propert(y/ies), the
        /// principal key's own propert(y/ies), and both remediations: re-point the FK at
        /// <c>FileAttachment.ID</c>, or opt out of this whole guard via
        /// <see cref="Enabled"/> = <see langword="false"/>.
        /// </summary>
        private static NotSupportedException BuildPrincipalKeyRejectionException(IEntityType entityType, IForeignKey fk, Type principalClrType, IReadOnlyKey principalKey)
        {
            var fkPropertyNames = string.Join(", ", fk.Properties.Select(p => p.Name));
            var principalKeyPropertyNames = string.Join(", ", principalKey.Properties.Select(p => p.Name));
            return new NotSupportedException(
                $"Issue #985: {entityType.ClrType.Name}.{fkPropertyNames} is a foreign key onto " +
                $"{principalClrType.Name} (a FileAttachment or a type derived from it) whose " +
                $"principal key is ({principalKeyPropertyNames}), not the single Guid " +
                $"{nameof(TopBasePoco.ID)} primary key. FileAttachmentSaveChangesGuard's " +
                $"resolution query (DCExtension.ResolveFileAttachmentIds/-Async) hardcodes " +
                $"`x.ID` — resolving a Guid-valued alternate key against it both permits a " +
                $"forged reference (an attacker whose own row's ID happens to equal the " +
                $"victim's alternate-key value is waved through) and rejects a legitimate one " +
                $"(a real alternate-key value is never found by ID). This model shape is not " +
                $"supported by the #824 SaveChanges boundary guard. To fix: either re-point " +
                $"{entityType.ClrType.Name}.{fkPropertyNames} at {nameof(FileAttachment)}." +
                $"{nameof(TopBasePoco.ID)} (remove the HasPrincipalKey(...) override for this " +
                $"relationship), or opt out of this guard PROCESS-WIDE via " +
                $"{nameof(FileAttachmentSaveChangesGuard)}.{nameof(Enabled)} = false — that single " +
                $"static switch is shared by every context in this process, not scoped to this " +
                $"one, and setting it false re-enables the exact Guid-alternate-key false allow " +
                $"this check exists to close (plus the rest of the #824 write-path protection " +
                $"this class's own doc comment describes).");
        }

        private static Guid? ExtractGuid(object? value)
        {
            if (value is Guid guid && guid != Guid.Empty)
            {
                return guid;
            }
            return null;
        }

        private readonly record struct Candidate(Type EntityClrType, string PropertyName, Guid Id, Type PrincipalClrType);

        /// <summary>
        /// Cheap-exit layer 2 + the "same unit of work" exception + the "already persisted, not a
        /// NEW reference" exception (Issue #824 Finding 4, adversarial review of PR #978): walks
        /// <c>ChangeTracker.Entries()</c> ONCE, collecting (a) every candidate FK write that needs
        /// resolving — an Added entry's non-empty FK value, or a Modified entry whose FK property
        /// is specifically <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry.IsModified"/>
        /// AND whose posted value does NOT match what is already persisted for that exact row (see
        /// below) — and (b) the id of every <see cref="FileAttachment"/> entry that is itself
        /// <see cref="EntityState.Added"/> in this SAME <c>SaveChanges</c> call. An id in set (b)
        /// is trusted without a database round trip: it is guaranteed to be INSERTed (and
        /// therefore PK-checked) in this very transaction. An <see cref="EntityState.Unchanged"/>
        /// or <see cref="EntityState.Modified"/> <see cref="FileAttachment"/> entry does NOT
        /// qualify — a manually <c>Attach</c>ed <see cref="FileAttachment"/> performs no INSERT
        /// and no PK check, which is the one real impersonation route this exception exists to
        /// close.
        /// <para>
        /// <b>Why "IsModified" alone is not the whole test (Finding 4):</b> WTM's edit path is
        /// detached-attach — <see cref="EmptyContext.UpdateEntity{T}"/> sets the WHOLE entity to
        /// <see cref="EntityState.Modified"/>, so EF marks EVERY scalar property
        /// <c>IsModified = true</c> regardless of whether the caller's request actually touched
        /// it. An earlier version of this guard treated ANY Modified-and-IsModified candidate as
        /// needing fresh resolution, unconditionally — which is exactly right for a value the
        /// caller is actually introducing, but ALSO rejected two shapes that introduce nothing
        /// new: (1) a non-<see cref="ITenant"/> row whose FK already, legitimately, points at a
        /// file owned by a DIFFERENT tenant than the editing caller (the row itself is shared by
        /// design — #824's threat model is about the FILE crossing tenants on a NEW write, not
        /// about an already-tenant-scoped ROW), and (2) a row whose FK already points at a
        /// NULL-tenant file (a documented, supported pattern — mainhost/pre-multi-tenancy uploads,
        /// see the #859 entry in <c>docs/production-readiness.md</c>) being re-posted unchanged by
        /// a real-tenant caller. Both are REGRESSIONS relative to <c>BaseCRUDVM</c>'s own
        /// #815-era precedent (<c>ApplyFileAttachmentResolution</c>), which already reverts an
        /// unresolvable posted value back to the entity's own pre-edit snapshot rather than
        /// rejecting the whole request whenever a legitimate prior value exists — #815 never
        /// forced re-validation of a value the caller did not actually change.
        /// </para>
        /// <para>
        /// The fix generalizes that SAME precedent to this generic, per-entity-type-agnostic
        /// boundary: for a Modified entry, <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry.GetDatabaseValues"/>
        /// (EF Core's own built-in mechanism for reading a tracked entity's CURRENTLY-persisted
        /// column values, independent of the change tracker's own — here untrustworthy —
        /// <c>OriginalValue</c>) fetches what is ACTUALLY in the database for this exact row RIGHT
        /// NOW, and the candidate is skipped (not even queued for resolution) when the posted
        /// value equals it: the caller is not introducing anything the row did not already have.
        /// A value that genuinely differs from what's persisted — the actual attack shape, and any
        /// legitimate change too — is still fully resolved as before. If <c>GetDatabaseValues()</c>
        /// returns <see langword="null"/> (the row does not exist — e.g. deleted concurrently, or
        /// this Modified entry has no persisted counterpart at all) this optimization simply does
        /// not apply and the candidate falls through to normal resolution — fail-closed, not a
        /// silent skip.
        /// </para>
        /// <para>
        /// <b>Accepted cost:</b> this adds one <c>GetDatabaseValues</c>(<c>Async</c>) round trip
        /// PER MODIFIED ENTITY that has at least one Modified-and-IsModified candidate FK — not
        /// batched across entities (unlike the resolution query itself). This is the same
        /// trade-off this class's own doc history already accepts elsewhere ("the duplicate
        /// sub-ms PK IN query on standard CRUD is accepted") — correctness for a real compatibility
        /// regression outweighs one more per-row query on an already-narrow subset of saves
        /// (models with an attachment FK, entities actually touching it). Batching this check is
        /// a possible future optimization, not attempted here.
        /// </para>
        /// </summary>
        private static (List<Candidate> Candidates, Dictionary<Guid, Type> SameUnitOfWorkAddedAttachmentTypes) CollectCandidates(
            EmptyContext context, IReadOnlyDictionary<IEntityType, FkAttachmentInfo[]> map)
        {
            var candidates = new List<Candidate>();
            var addedAttachmentTypes = new Dictionary<Guid, Type>();

            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State == EntityState.Added && entry.Entity is FileAttachment addedAttachment)
                {
                    // Issue #824 Finding 6 (PR #978): the SPECIFIC runtime type, not just "some
                    // FileAttachment-derived type" — see CollectCandidatesAsync's matching branch
                    // below, and the class-level rationale on FkAttachmentInfo, for why the
                    // per-candidate type check below needs this.
                    addedAttachmentTypes[addedAttachment.ID] = entry.Entity.GetType();
                }

                if (!map.TryGetValue(entry.Metadata, out var fkInfos))
                {
                    continue;
                }

                if (entry.State == EntityState.Added)
                {
                    foreach (var info in fkInfos)
                    {
                        var id = ExtractGuid(entry.Property(info.PropertyName).CurrentValue);
                        if (id is Guid addedId)
                        {
                            candidates.Add(new Candidate(entry.Entity.GetType(), info.PropertyName, addedId, info.PrincipalClrType));
                        }
                    }
                }
                else if (entry.State == EntityState.Modified)
                {
                    // Issue #824 Finding 4: only fetched when at least one candidate FK on this
                    // entry is actually IsModified — never for a Modified entity whose attachment
                    // FK(s) are untouched, which is the common case and must stay on the cheap
                    // path (cheap-exit layer 2 still applies at the Guard level for a whole
                    // SaveChanges call with no candidates at all).
                    Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues? databaseValues = null;
                    var databaseValuesFetched = false;

                    foreach (var info in fkInfos)
                    {
                        var property = entry.Property(info.PropertyName);
                        if (!property.IsModified)
                        {
                            continue;
                        }
                        var id = ExtractGuid(property.CurrentValue);
                        if (id is not Guid modifiedId)
                        {
                            continue;
                        }
                        if (!databaseValuesFetched)
                        {
                            databaseValues = entry.GetDatabaseValues();
                            databaseValuesFetched = true;
                        }
                        var persistedId = databaseValues != null ? ExtractGuid(databaseValues[info.PropertyName]) : null;
                        if (persistedId == modifiedId)
                        {
                            continue; // Already persisted with this exact value -- not a NEW reference.
                        }
                        candidates.Add(new Candidate(entry.Entity.GetType(), info.PropertyName, modifiedId, info.PrincipalClrType));
                    }
                }
            }

            return (candidates, addedAttachmentTypes);
        }

        /// <summary>
        /// Async counterpart of <see cref="CollectCandidates"/> — identical logic, but awaits
        /// <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry.GetDatabaseValuesAsync"/>
        /// instead of calling <c>GetDatabaseValues()</c> synchronously, for the same reason
        /// <see cref="GuardAsync"/> awaits the resolution query instead of calling
        /// <see cref="Guard"/>'s sync version — an async SaveChanges path must never block a
        /// ThreadPool thread on synchronous I/O.
        /// </summary>
        private static async Task<(List<Candidate> Candidates, Dictionary<Guid, Type> SameUnitOfWorkAddedAttachmentTypes)> CollectCandidatesAsync(
            EmptyContext context, IReadOnlyDictionary<IEntityType, FkAttachmentInfo[]> map, CancellationToken cancellationToken)
        {
            var candidates = new List<Candidate>();
            var addedAttachmentTypes = new Dictionary<Guid, Type>();

            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State == EntityState.Added && entry.Entity is FileAttachment addedAttachment)
                {
                    addedAttachmentTypes[addedAttachment.ID] = entry.Entity.GetType();
                }

                if (!map.TryGetValue(entry.Metadata, out var fkInfos))
                {
                    continue;
                }

                if (entry.State == EntityState.Added)
                {
                    foreach (var info in fkInfos)
                    {
                        var id = ExtractGuid(entry.Property(info.PropertyName).CurrentValue);
                        if (id is Guid addedId)
                        {
                            candidates.Add(new Candidate(entry.Entity.GetType(), info.PropertyName, addedId, info.PrincipalClrType));
                        }
                    }
                }
                else if (entry.State == EntityState.Modified)
                {
                    Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues? databaseValues = null;
                    var databaseValuesFetched = false;

                    foreach (var info in fkInfos)
                    {
                        var property = entry.Property(info.PropertyName);
                        if (!property.IsModified)
                        {
                            continue;
                        }
                        var id = ExtractGuid(property.CurrentValue);
                        if (id is not Guid modifiedId)
                        {
                            continue;
                        }
                        if (!databaseValuesFetched)
                        {
                            databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false);
                            databaseValuesFetched = true;
                        }
                        var persistedId = databaseValues != null ? ExtractGuid(databaseValues[info.PropertyName]) : null;
                        if (persistedId == modifiedId)
                        {
                            continue;
                        }
                        candidates.Add(new Candidate(entry.Entity.GetType(), info.PropertyName, modifiedId, info.PrincipalClrType));
                    }
                }
            }

            return (candidates, addedAttachmentTypes);
        }

        /// <summary>
        /// Synchronous guard, called from <see cref="EmptyContext.SaveChanges()"/> and
        /// <see cref="EmptyContext.SaveChanges(bool)"/>, BEFORE <c>ApplyAuditFields</c>. Throws
        /// <see cref="UnresolvableFileAttachmentReferenceException"/> and persists nothing when a
        /// posted FileAttachment reference cannot be resolved; otherwise returns normally and
        /// lets the caller's own <c>base.SaveChanges()</c> proceed.
        /// </summary>
        public static void Guard(EmptyContext context)
        {
            if (!Enabled)
            {
                return;
            }
            var map = GetOrBuildMap(context.Model);
            if (map.Count == 0)
            {
                return; // Layer 1 cheap exit: this model has no FileAttachment-pointing FK anywhere.
            }

            var (candidates, sameUnitOfWorkAddedTypes) = CollectCandidates(context, map);
            if (candidates.Count == 0)
            {
                return; // Layer 2 cheap exit: nothing Added/Modified touches such an FK right now.
            }

            var idsNeedingResolution = candidates
                .Where(c => !IsTrustedSameUnitOfWork(c, sameUnitOfWorkAddedTypes))
                .Select(c => c.Id)
                .Distinct()
                .ToList();
            if (idsNeedingResolution.Count == 0)
            {
                return; // Every candidate is covered by a same-unit-of-work Added attachment.
            }

            var outcome = context.ResolveFileAttachmentIds(idsNeedingResolution);
            if (!outcome.Succeeded)
            {
                LogResolutionFailure(outcome.Failure);
                throw UnresolvableFileAttachmentReferenceException.ForResolutionFailure(outcome.Failure);
            }

            foreach (var candidate in candidates)
            {
                if (IsTrustedSameUnitOfWork(candidate, sameUnitOfWorkAddedTypes))
                {
                    continue;
                }
                if (!outcome.ResolvedIds.Contains(candidate.Id))
                {
                    LogRejection(candidate);
                    throw new UnresolvableFileAttachmentReferenceException(candidate.EntityClrType, candidate.PropertyName, candidate.Id);
                }
            }
        }

        /// <summary>
        /// Issue #824 Finding 6 (adversarial review of PR #978): the "same unit of work" trust
        /// exception is safe ONLY when the Added attachment entry's OWN runtime type is the FK's
        /// declared principal type or a MORE-derived subtype of it — never merely "some
        /// FileAttachment-derived type happens to share this id".
        /// <para>
        /// Without this check, a shell object attack defeats it for TPC (Table-Per-Concrete-Type)
        /// mapping specifically: TPH and TPT share (or link) one physical table across the whole
        /// hierarchy, so an Added <see cref="FileAttachment"/> shell carrying a REAL, EXISTING
        /// derived-row's id would collide on INSERT (a PK violation — the very mechanism this
        /// exception's doc comment relies on: "it will be INSERTed, therefore PK-checked"). TPC
        /// gives EACH concrete type its OWN separate table and PK space — an Added base-typed
        /// <see cref="FileAttachment"/> shell with <c>ID = victimSignedFileId</c> inserts into the
        /// (unrelated) base table with NO collision, while the actual FK constraint on a
        /// dependent whose relationship principal is the DERIVED type (e.g. <c>SignedFile</c>)
        /// references the DERIVED type's own table — already satisfied by the victim's real,
        /// pre-existing row there, regardless of whether the shell's own insert succeeds. The old,
        /// flat <c>HashSet&lt;Guid&gt;</c> trust check could not tell "an Added <c>SignedFile</c>"
        /// from "an Added <c>FileAttachment</c> shell claiming a <c>SignedFile</c>'s id" apart —
        /// both satisfied <c>entry.Entity is FileAttachment</c>. Requiring the FK's OWN principal
        /// type to accept the Added entry's ACTUAL type closes this: a base-typed shell is not
        /// assignable to a derived-typed principal requirement, so it no longer qualifies for the
        /// exception and falls through to the same tenant-scoped resolution query every other
        /// candidate gets.
        /// </para>
        /// </summary>
        private static bool IsTrustedSameUnitOfWork(Candidate candidate, Dictionary<Guid, Type> sameUnitOfWorkAddedTypes)
        {
            return sameUnitOfWorkAddedTypes.TryGetValue(candidate.Id, out var addedType)
                && candidate.PrincipalClrType.IsAssignableFrom(addedType);
        }

        /// <summary>
        /// Async counterpart of <see cref="Guard"/> — same logic, the resolution query awaited
        /// instead of run synchronously. Called from
        /// <see cref="EmptyContext.SaveChangesAsync(bool, CancellationToken)"/> and
        /// <see cref="EmptyContext.SaveChangesAsync(CancellationToken)"/>, before
        /// <c>ApplyAuditFields</c>.
        /// </summary>
        public static async Task GuardAsync(EmptyContext context, CancellationToken cancellationToken = default)
        {
            if (!Enabled)
            {
                return;
            }
            var map = GetOrBuildMap(context.Model);
            if (map.Count == 0)
            {
                return;
            }

            var (candidates, sameUnitOfWorkAddedTypes) = await CollectCandidatesAsync(context, map, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return;
            }

            var idsNeedingResolution = candidates
                .Where(c => !IsTrustedSameUnitOfWork(c, sameUnitOfWorkAddedTypes))
                .Select(c => c.Id)
                .Distinct()
                .ToList();
            if (idsNeedingResolution.Count == 0)
            {
                return;
            }

            var outcome = await context.ResolveFileAttachmentIdsAsync(idsNeedingResolution, cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                LogResolutionFailure(outcome.Failure);
                throw UnresolvableFileAttachmentReferenceException.ForResolutionFailure(outcome.Failure);
            }

            foreach (var candidate in candidates)
            {
                if (IsTrustedSameUnitOfWork(candidate, sameUnitOfWorkAddedTypes))
                {
                    continue;
                }
                if (!outcome.ResolvedIds.Contains(candidate.Id))
                {
                    LogRejection(candidate);
                    throw new UnresolvableFileAttachmentReferenceException(candidate.EntityClrType, candidate.PropertyName, candidate.Id);
                }
            }
        }
    }
}
