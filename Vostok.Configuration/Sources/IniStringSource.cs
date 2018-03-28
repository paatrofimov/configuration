﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;

namespace Vostok.Configuration.Sources
{
    /// <inheritdoc />
    /// <summary>
    /// Json converter to RawSettings tree from string
    /// </summary>
    public class IniStringSource : IConfigurationSource
    {
        private readonly string ini;
        private int currentLine;

        /// <summary>
        /// Creating ini converter
        /// </summary>
        /// <param name="ini">Ini file data in string</param>
        public IniStringSource(string ini)
        {
            this.ini = ini;
            currentLine = -1;
        }

        public RawSettings Get() =>
            string.IsNullOrWhiteSpace(ini) ? null : ParseIni(ini);

        public IObservable<RawSettings> Observe() => 
            Observable.Empty<RawSettings>();

        private RawSettings ParseIni(string text)
        {
            var res = new RawSettingsEditable();
            var section = res;

            var lines = text
                .Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l));
            foreach (var line in lines)
            {
                currentLine++;
                if (line.StartsWith("#") || line.StartsWith(";"))
                    continue;
                if (line.StartsWith("[") && line.EndsWith("]") && line.Length > 2 && !line.Contains(" "))
                    section = ParseSection(line.Substring(1, line.Length - 2), res);
                else
                {
                    var pair = line.Split(new[] {'='}, 2).Select(s => s.Trim()).ToArray();
                    if (pair.Length == 2 && pair[0].Length > 0 && !pair[0].Contains(" "))
                        ParsePair(pair[0], pair[1], section);
                    else
                        throw new FormatException($"Wrong ini file ({currentLine}): line \"{line}\"");
                }
            }
            currentLine = -1;

            if (res.Children.Count == 0 && res.ChildrenByKey.Count == 0 && res.Value == null)
                return null;
            return (RawSettings)res;
        }

        private RawSettingsEditable ParseSection(string section, RawSettingsEditable settings)
        {
            section = section.Replace(" ", "");

            if (settings.ChildrenByKey.ContainsKey(section))
                throw new FormatException($"Wrong ini file ({currentLine}): section \"{section}\" already exists");
            var res = new RawSettingsEditable();
            settings.ChildrenByKey.Add(section, res);
            return res;
        }

        private void ParsePair(string key, string value, RawSettingsEditable settings)
        {
            var keys = key.Replace(" ", "").Split('.');
            var isObj = false;
            var obj = settings;
            for (var i = 0; i < keys.Length; i++)
            {
                if (i == keys.Length - 1)
                {
                    if (obj.ChildrenByKey.ContainsKey(keys[i]))
                    {
                        var val = obj.ChildrenByKey[keys[i]].Value;
                        if (val != null)
                            throw new FormatException($"Wrong ini file ({currentLine}): key \"{keys[i]}\" with value \"{val}\" already exists");
                        else
                            obj.ChildrenByKey[keys[i]].Value = value;
                    }
                    else
                        obj.ChildrenByKey.Add(keys[i], new RawSettingsEditable(value));
                }
                else if (!obj.ChildrenByKey.ContainsKey(keys[i]))
                {
                    var newObj = new RawSettingsEditable();
                    obj.ChildrenByKey.Add(keys[i], newObj);
                    obj = newObj;
                }
                else
                {
                    obj = obj.ChildrenByKey[keys[i]];
                    if (obj.ChildrenByKey == null)
                        obj.ChildrenByKey = new Dictionary<string, RawSettingsEditable>();
                }
                isObj = !isObj;
            }
        }

        public void Dispose() { }

        private class RawSettingsEditable
        {
            public RawSettingsEditable()
            {
                ChildrenByKey = new Dictionary<string, RawSettingsEditable>();
                Children = new List<RawSettingsEditable>();
            }
            public RawSettingsEditable(string value)
            {
                Value = value;
            }

            public string Value { get; set; }
            public IDictionary<string, RawSettingsEditable> ChildrenByKey { get; set; }
            public IList<RawSettingsEditable> Children { get; }

            public static explicit operator RawSettings(RawSettingsEditable settings)
            {
                var dict = settings.ChildrenByKey == null || settings.ChildrenByKey.Count == 0
                    ? null
                    : settings.ChildrenByKey?.ToDictionary(d => d.Key, d => (RawSettings)d.Value);
                var list = settings.Children == null || settings.Children.Count == 0
                    ? null
                    : settings.Children?.Select(i => (RawSettings)i).ToList();

                return new RawSettings(dict, list, settings.Value);
            }
        }
    }
}