// SPDX-License-Identifier: AGPL-3.0-only
/**
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024 Caleb, K4PHP
*
*/

using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;

namespace ZelloGateway
{
    /// <summary>
    /// Zello Alias
    /// </summary>
    public class ZelloAlias
    {
        public int Rid { get; set; }
        public string Alias { get; set; }
    }

    /// <summary>
    /// Zello Config
    /// </summary>
    public class ZelloConfig
    {
        public List<ZelloAlias> ZelloAliases { get; set; }
    }

    /// <summary>
    /// Zello Alias Lookup
    /// </summary>
    public class ZelloAliasLookup
    {
        private readonly Dictionary<string, int> _aliasToRidMap;

        /// <summary>
        /// Creates an instance of <see cref="ZelloAliasLookup"/>
        /// </summary>
        /// <param name="filePath"></param>
        public ZelloAliasLookup(string filePath)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            ZelloConfig config;
            using (var reader = new StreamReader(filePath))
            {
                config = deserializer.Deserialize<ZelloConfig>(reader);
            }

            _aliasToRidMap = config.ZelloAliases
                .ToDictionary(
                    x => x.Alias.Replace(" ", "").ToLowerInvariant(),
                    x => x.Rid
                );
        }

        /// <summary>
        /// Looks up the RID for a given alias, ignoring case and spaces.
        /// Returns -1 if no match is found.
        /// </summary>
        /// <param name="alias">The alias to look up</param>
        /// <returns>The RID associated with the alias, or -1 if not found</returns>
        public uint GetRidByAlias(string alias)
        {
            if (alias == null)
                return 0;

            var sanitizedAlias = alias.Replace(" ", "").ToLowerInvariant();
            return (uint)(_aliasToRidMap.TryGetValue(sanitizedAlias, out int rid) ? rid : 0);
        }
    }
}
