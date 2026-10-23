using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WalkingTec.Mvvm.Core.Json
{
    /// <summary>
    /// Keeps the storage details of a <see cref="FileAttachment"/> out of JSON output while leaving JSON input and the entity itself untouched.
    /// <para>
    /// A view model that loads a <see cref="FileAttachment"/> navigation (for example <c>BaseCRUDVM.GetById</c> fills <c>Student.Photo</c>) would otherwise write
    /// where and how the file is stored: <see cref="FileAttachment.Path"/>, <see cref="FileAttachment.ExtraInfo"/>, <see cref="FileAttachment.SaveMode"/>,
    /// <see cref="FileAttachment.HandlerInfo"/>, and the bytes (<see cref="FileAttachment.FileData"/>). What a client needs is still written:
    /// <c>ID</c>, <c>FileName</c>, <c>FileExt</c>, <c>Length</c>, <c>UploadTime</c> (and <c>TenantCode</c>).
    /// </para>
    /// <para>
    /// The exclusion is done on the serialization side only (<see cref="JsonPropertyInfo.ShouldSerialize"/>), so a request body that contains these members still
    /// binds, and server side code (<c>WtmFileProvider</c>, import view models, file handlers) reads the same properties as before. The model itself carries no
    /// <c>[JsonIgnore]</c>, which would also block the input.
    /// </para>
    /// </summary>
    public static class FileAttachmentOutputExclusion
    {
        /// <summary>The members of <see cref="FileAttachment"/> that are never written to an output.</summary>
        public static readonly string[] ExcludedMembers =
        {
            nameof(FileAttachment.Path),
            nameof(FileAttachment.ExtraInfo),
            nameof(FileAttachment.SaveMode),
            nameof(FileAttachment.HandlerInfo),
            nameof(FileAttachment.FileData)
        };

        /// <summary>
        /// A <see cref="JsonTypeInfo"/> modifier: for <see cref="FileAttachment"/> and its subclasses, the members in <see cref="ExcludedMembers"/> are never
        /// written. Other types (including other classes that have a <c>Path</c> property) are not touched.
        /// </summary>
        public static void Modify(JsonTypeInfo typeInfo)
        {
            if (typeInfo == null || typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(FileAttachment).IsAssignableFrom(typeInfo.Type))
            {
                return;
            }
            foreach (var property in typeInfo.Properties)
            {
                if (IsStorageMember(property))
                {
                    property.ShouldSerialize = static (_, _) => false;
                }
            }
        }

        /// <summary>
        /// Adds <see cref="Modify"/> to the type info resolver of <paramref name="options"/>. An existing resolver is kept and wrapped
        /// (not replaced); without one, the reflection based <see cref="DefaultJsonTypeInfoResolver"/> is used.
        /// Call it before the options are first used for serialization.
        /// </summary>
        public static JsonSerializerOptions ExcludeFileStorageDetailsFromOutput(this JsonSerializerOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            IJsonTypeInfoResolver resolver = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            options.TypeInfoResolver = resolver.WithAddedModifier(Modify);
            return options;
        }

        private static bool IsStorageMember(JsonPropertyInfo property)
        {
            if (property.AttributeProvider is MemberInfo member)
            {
                // only the members FileAttachment declares: a subclass' own property of the same name is the subclass' business
                return member.DeclaringType == typeof(FileAttachment) && Array.IndexOf(ExcludedMembers, member.Name) >= 0;
            }
            return Array.Exists(ExcludedMembers, x => string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
