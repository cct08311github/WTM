using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Reflection;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Rules of the single-property update <c>/_Framework/UpdateModelProperty</c>, independent of the mode and of the list of editable
    /// properties the page declares: which members of a record this endpoint never writes, and how the text of the request is turned into
    /// the value of a property (strictly: text that is not a value of the property type is not replaced by a default value).
    /// </summary>
    public static class ModelPropertyUpdateRules
    {
        /// <summary>
        /// The property a request may set, or null when this endpoint never writes it: only public instance properties with a public setter
        /// whose type is a scalar (string, number, bool, date, Guid, enum or a nullable of those). Never written: the key, the tenant code
        /// (<see cref="ITenant"/>), the validity flag (<see cref="IPersistPoco"/>), the create and update stamps (<see cref="IBasePoco"/>),
        /// and properties marked <see cref="NotMappedAttribute"/> or <see cref="CanNotEditAttribute"/>. The name is compared exactly.
        /// </summary>
        public static PropertyInfo FindWritableProperty(Type entityType, string name)
        {
            if (entityType == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            PropertyInfo property;
            try
            {
                property = entityType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    ?? entityType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                return null;
            }
            if (property == null || property.CanRead == false || property.GetIndexParameters().Length > 0
                || property.SetMethod == null || property.SetMethod.IsPublic == false || property.SetMethod.IsStatic
                || IsScalar(property.PropertyType) == false
                || IsRefusedMember(entityType, property.Name)
                || property.GetCustomAttribute<NotMappedAttribute>() != null || property.GetCustomAttribute<CanNotEditAttribute>() != null)
            {
                return null;
            }
            return property;
        }

        /// <summary>Members the endpoint refuses for every record type, whatever the reference lists.</summary>
        public static bool IsRefusedMember(Type entityType, string name)
        {
            if (name == "ID")
            {
                return true;
            }
            if (name == nameof(ITenant.TenantCode) && typeof(ITenant).IsAssignableFrom(entityType))
            {
                return true;
            }
            if (name == nameof(IPersistPoco.IsValid) && typeof(IPersistPoco).IsAssignableFrom(entityType))
            {
                return true;
            }
            if (typeof(IBasePoco).IsAssignableFrom(entityType)
                && (name == nameof(IBasePoco.CreateTime) || name == nameof(IBasePoco.CreateBy) || name == nameof(IBasePoco.UpdateTime) || name == nameof(IBasePoco.UpdateBy)))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Turns the text of a request into a value of <paramref name="type"/>. Numbers are parsed with the invariant culture; dates accept
        /// ISO 8601 (and the other formats the invariant culture knows). Empty text is null for a string or a nullable type and not accepted
        /// otherwise. A string has its backslashes turned into slashes, as the endpoint always did. Returns false when the text is not a value
        /// of the type (it is never replaced by a default value).
        /// </summary>
        public static bool TryConvert(Type type, string value, out object result)
        {
            result = null;
            var underlying = Nullable.GetUnderlyingType(type);
            var target = underlying ?? type;
            if (target == typeof(string))
            {
                result = string.IsNullOrEmpty(value) ? null : value.Replace("\\", "/");
                return true;
            }
            if (string.IsNullOrEmpty(value))
            {
                return underlying != null;
            }
            var invariant = CultureInfo.InvariantCulture;
            const NumberStyles integer = NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
            const NumberStyles fixedPoint = integer | NumberStyles.AllowDecimalPoint;
            const NumberStyles floating = fixedPoint | NumberStyles.AllowExponent;
            const DateTimeStyles dates = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind;
            bool ok;
            if (target == typeof(bool)) { ok = bool.TryParse(value, out var v); result = v; }
            else if (target == typeof(byte)) { ok = byte.TryParse(value, integer, invariant, out var v); result = v; }
            else if (target == typeof(sbyte)) { ok = sbyte.TryParse(value, integer, invariant, out var v); result = v; }
            else if (target == typeof(short)) { ok = short.TryParse(value, integer, invariant, out var v); result = v; }
            else if (target == typeof(ushort)) { ok = ushort.TryParse(value, integer, invariant, out var v); result = v; }
            else if (target == typeof(int)) { ok = int.TryParse(value, integer, invariant, out var v); result = v; }
            else if (target == typeof(uint)) { ok = uint.TryParse(value, integer, invariant, out var v); result = v; }
            else if (target == typeof(long)) { ok = long.TryParse(value, integer, invariant, out var v); result = v; }
            else if (target == typeof(ulong)) { ok = ulong.TryParse(value, integer, invariant, out var v); result = v; }
            else if (target == typeof(decimal)) { ok = decimal.TryParse(value, fixedPoint, invariant, out var v); result = v; }
            else if (target == typeof(double)) { ok = double.TryParse(value, floating, invariant, out var v) && double.IsFinite(v); result = v; }
            else if (target == typeof(float)) { ok = float.TryParse(value, floating, invariant, out var v) && float.IsFinite(v); result = v; }
            else if (target == typeof(DateTime)) { ok = DateTime.TryParse(value, invariant, dates, out var v); result = v; }
            else if (target == typeof(DateTimeOffset)) { ok = DateTimeOffset.TryParse(value, invariant, dates, out var v); result = v; }
            else if (target == typeof(Guid)) { ok = Guid.TryParse(value, out var v); result = v; }
            else if (target.IsEnum)
            {
                ok = Enum.TryParse(target, value.Trim(), true, out var v)
                    && (Enum.IsDefined(target, v) || target.IsDefined(typeof(FlagsAttribute), false));
                result = v;
            }
            else
            {
                ok = false;
            }
            if (ok == false)
            {
                result = null;
            }
            return ok;
        }

        private static bool IsScalar(Type type)
        {
            var target = Nullable.GetUnderlyingType(type) ?? type;
            return target == typeof(string) || target == typeof(bool) || target == typeof(byte) || target == typeof(sbyte)
                || target == typeof(short) || target == typeof(ushort) || target == typeof(int) || target == typeof(uint)
                || target == typeof(long) || target == typeof(ulong) || target == typeof(decimal) || target == typeof(double)
                || target == typeof(float) || target == typeof(DateTime) || target == typeof(DateTimeOffset) || target == typeof(Guid)
                || target.IsEnum;
        }
    }
}
