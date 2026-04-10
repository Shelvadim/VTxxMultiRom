using System;
using System.Collections.Generic;
using System.Linq;

namespace VT03Builder.Services.Targets
{
    /// <summary>
    /// Central lookup: target Id string → IHardwareTarget.
    ///
    /// To add support for a new hardware target:
    ///   1. Create a new target class in Services/Targets/
    ///   2. Add one line to the Targets array below.
    ///   No other file needs to change.
    /// </summary>
    public static class TargetRegistry
    {
        private static readonly IHardwareTarget[] Targets =
        {
            new VtxxOneBusTarget(),
            new CoolBoyTarget(),
            // Future: new CoolGirlTarget(), new CoolBabyTarget()
        };

        /// <summary>
        /// Returns the target with the given Id, or null if not registered.
        /// </summary>
        public static IHardwareTarget? Get(string id) =>
            Targets.FirstOrDefault(t => t.Id == id);

        /// <summary>
        /// Returns the target with the given Id, or throws if not found.
        /// Use in Build() where a missing target is a programming error.
        /// </summary>
        public static IHardwareTarget GetRequired(string id) =>
            Get(id) ?? throw new InvalidOperationException(
                $"Hardware target '{id}' not registered. " +
                $"Available: {string.Join(", ", All.Select(t => t.Id))}");

        /// <summary>All registered hardware targets (for UI dropdown population).</summary>
        public static IEnumerable<IHardwareTarget> All => Targets;

        /// <summary>The default target used when TargetId is not specified.</summary>
        public const string DefaultId = "vtxx";
    }
}
