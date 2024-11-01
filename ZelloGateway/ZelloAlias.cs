using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;

namespace ZelloGateway
{
    public class ZelloAlias
    {
        public int Rid { get; set; }
        public string Alias { get; set; }
    }

    public class ZelloConfig
    {
        public List<ZelloAlias> ZelloAliases { get; set; }
    }

    public class ZelloAliasLookup
    {
        private readonly Dictionary<string, int> _aliasToRidMap;

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
            var sanitizedAlias = alias.Replace(" ", "").ToLowerInvariant();
            return (uint)(_aliasToRidMap.TryGetValue(sanitizedAlias, out int rid) ? rid : -1);
        }
    }
}
