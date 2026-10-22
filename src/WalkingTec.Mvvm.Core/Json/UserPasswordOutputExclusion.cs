using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WalkingTec.Mvvm.Core.Json
{
    /// <summary>
    /// Keeps <see cref="FrameworkUserBase.Password"/> out of JSON output while leaving JSON input untouched.
    /// <para>
    /// The exclusion is done on the serialization side only (<see cref="JsonPropertyInfo.ShouldSerialize"/>), so a request body that
    /// contains <c>Password</c> still binds: creating a user and changing a password work as before. The model itself carries no
    /// <c>[JsonIgnore]</c>, which would also block the input.
    /// </para>
    /// </summary>
    public static class UserPasswordOutputExclusion
    {
        /// <summary>
        /// A <see cref="JsonTypeInfo"/> modifier: for <see cref="FrameworkUserBase"/> and its subclasses, the property named
        /// <c>Password</c> is never written. Other types (including other classes that have a <c>Password</c> property) are not touched.
        /// </summary>
        public static void Modify(JsonTypeInfo typeInfo)
        {
            if (typeInfo == null || typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(FrameworkUserBase).IsAssignableFrom(typeInfo.Type))
            {
                return;
            }
            foreach (var property in typeInfo.Properties)
            {
                if (IsPasswordProperty(property))
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
        public static JsonSerializerOptions ExcludeUserPasswordFromOutput(this JsonSerializerOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            IJsonTypeInfoResolver resolver = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            options.TypeInfoResolver = resolver.WithAddedModifier(Modify);
            return options;
        }

        private static bool IsPasswordProperty(JsonPropertyInfo property)
        {
            if (property.AttributeProvider is MemberInfo member)
            {
                return string.Equals(member.Name, nameof(FrameworkUserBase.Password), StringComparison.Ordinal);
            }
            return string.Equals(property.Name, nameof(FrameworkUserBase.Password), StringComparison.OrdinalIgnoreCase);
        }
    }
}
