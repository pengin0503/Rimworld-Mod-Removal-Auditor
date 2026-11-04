using System;

namespace ModRemovalAuditor.Audit
{
    /// <summary>
    /// Normalized (trimmed, lower-case invariant) package id. Mod display names are never used for matching.
    /// </summary>
    public readonly struct ModKey : IEquatable<ModKey>
    {
        private const string CorePackageId = "ludeon.rimworld";

        private readonly string value;

        private ModKey(string value)
        {
            this.value = value;
        }

        public string Value => value ?? string.Empty;

        public bool IsEmpty => string.IsNullOrEmpty(value);

        /// <summary>Core or an official expansion (ludeon.rimworld, ludeon.rimworld.*).</summary>
        public bool IsLudeonOfficial =>
            value != null && (value == CorePackageId || value.StartsWith(CorePackageId + ".", StringComparison.Ordinal));

        public static ModKey Parse(string packageId)
        {
            if (!TryParse(packageId, out ModKey key))
            {
                throw new ArgumentException("Package id must not be empty.", nameof(packageId));
            }
            return key;
        }

        public static bool TryParse(string packageId, out ModKey key)
        {
            if (string.IsNullOrWhiteSpace(packageId))
            {
                key = default;
                return false;
            }
            key = new ModKey(packageId.Trim().ToLowerInvariant());
            return true;
        }

        public bool Equals(ModKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is ModKey other && Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

        public static bool operator ==(ModKey a, ModKey b) => a.Equals(b);

        public static bool operator !=(ModKey a, ModKey b) => !a.Equals(b);

        public override string ToString() => Value;
    }

    /// <summary>Serializable-free value identity of a def: (database def type name, defName).</summary>
    public readonly struct DefIdentity : IEquatable<DefIdentity>
    {
        public DefIdentity(string defType, string defName)
        {
            DefType = defType ?? string.Empty;
            DefName = defName ?? string.Empty;
        }

        public string DefType { get; }

        public string DefName { get; }

        public bool Equals(DefIdentity other) =>
            string.Equals(DefType, other.DefType, StringComparison.Ordinal) &&
            string.Equals(DefName, other.DefName, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is DefIdentity other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(DefType ?? string.Empty), StringComparer.Ordinal.GetHashCode(DefName ?? string.Empty));

        public override string ToString() => DefType + "/" + DefName;
    }
}
