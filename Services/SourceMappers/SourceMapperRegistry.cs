using System.Collections.Generic;
using System.Linq;
using VT03Builder.Models;

namespace VT03Builder.Services.SourceMappers
{
    /// <summary>
    /// Central lookup: NES source mapper number → ISourceMapperHandler.
    ///
    /// To add support for a new NES source mapper:
    ///   1. Create a new handler class in Services/SourceMappers/
    ///   2. Add one line to the Handlers array below.
    ///   No other file needs to change.
    /// </summary>
    public static class SourceMapperRegistry
    {
        private static readonly ISourceMapperHandler[] Handlers =
        {
            new NromHandler(),
            new Mmc3Handler(),
            // Future: new Mmc1Handler(), new UxRomHandler(), new CnRomHandler()
        };

        /// <summary>
        /// Returns the handler for the given NES mapper number,
        /// or null if not supported.
        /// </summary>
        public static ISourceMapperHandler? Get(int nesMapper) =>
            Handlers.FirstOrDefault(h => h.SupportedMappers.Contains(nesMapper));

        /// <summary>True if the NES mapper number is supported by any handler.</summary>
        public static bool IsSupported(int nesMapper) =>
            Get(nesMapper) != null;

        /// <summary>All NES mapper numbers supported across all registered handlers.</summary>
        public static IEnumerable<int> AllSupportedMappers =>
            Handlers.SelectMany(h => h.SupportedMappers);
    }
}
