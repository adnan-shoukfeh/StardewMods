using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ContentPatcher.Framework.Conditions;
using ContentPatcher.Framework.ConfigModels;
using ContentPatcher.Framework.Tokens;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace ContentPatcher.Framework.Patches
{
    /// <summary>Metadata for a data to edit into a data file.</summary>
    internal class EditDataPatch : Patch
    {
        /*********
        ** Properties
        *********/
        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;

        /// <summary>The data records to edit.</summary>
        private readonly IDictionary<string, TokenString> Records;

        /// <summary>The data fields to edit.</summary>
        private readonly IDictionary<string, IDictionary<int, TokenString>> Fields;

        /// <summary>The token strings which contain mutable tokens.</summary>
        private readonly TokenString[] MutableTokenStrings;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="logName">A unique name for this patch shown in log messages.</param>
        /// <param name="contentPack">The content pack which requested the patch.</param>
        /// <param name="assetName">The normalised asset name to intercept.</param>
        /// <param name="conditions">The conditions which determine whether this patch should be applied.</param>
        /// <param name="records">The data records to edit.</param>
        /// <param name="fields">The data fields to edit.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="normaliseAssetName">Normalise an asset name.</param>
        public EditDataPatch(string logName, ManagedContentPack contentPack, TokenString assetName, ConditionDictionary conditions, IDictionary<string, TokenString> records, IDictionary<string, IDictionary<int, TokenString>> fields, IMonitor monitor, Func<string, string> normaliseAssetName)
            : base(logName, PatchType.EditData, contentPack, assetName, conditions, normaliseAssetName)
        {
            this.Records = records;
            this.Fields = fields;
            this.Monitor = monitor;
            this.MutableTokenStrings = this.GetMutableTokens(records, fields).ToArray();
        }

        /// <summary>Update the patch data when the context changes.</summary>
        /// <param name="context">Provides access to contextual tokens.</param>
        /// <returns>Returns whether the patch data changed.</returns>
        public override bool UpdateContext(IContext context)
        {
            bool changed = base.UpdateContext(context);

            foreach (TokenString str in this.MutableTokenStrings)
            {
                if (str.UpdateContext(context))
                    changed = true;
            }

            return changed;
        }

        /// <summary>Get the tokens used by this patch in its fields.</summary>
        public override IEnumerable<TokenName> GetTokensUsed()
        {
            if (this.MutableTokenStrings.Length == 0)
                return base.GetTokensUsed();

            return base
                .GetTokensUsed()
                .Union(this.MutableTokenStrings.SelectMany(p => p.Tokens));
        }

        /// <summary>Apply the patch to a loaded asset.</summary>
        /// <typeparam name="T">The asset type.</typeparam>
        /// <param name="asset">The asset to edit.</param>
        /// <exception cref="NotSupportedException">The current patch type doesn't support editing assets.</exception>
        public override void Edit<T>(IAssetData asset)
        {
            // validate
            if (!typeof(T).IsGenericType || typeof(T).GetGenericTypeDefinition() != typeof(Dictionary<,>))
            {
                this.Monitor.Log($"Can't apply data patch \"{this.LogName}\" to {this.AssetName}: this file isn't a data file (found {(typeof(T) == typeof(Texture2D) ? "image" : typeof(T).Name)}).", LogLevel.Warn);
                return;
            }

            // get dictionary's key type
            Type keyType = typeof(T).GetGenericArguments().FirstOrDefault();
            if (keyType == null)
                throw new InvalidOperationException("Can't parse the asset's dictionary key type.");

            // get underlying apply method
            MethodInfo method = this.GetType().GetMethod(nameof(this.ApplyImpl), BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("Can't fetch the internal apply method.");

            // invoke method
            method
                .MakeGenericMethod(keyType)
                .Invoke(this, new object[] { asset });
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get the token strings which contain tokens and whose values may change.</summary>
        /// <param name="records">The data records to edit.</param>
        /// <param name="fields">The data fields to edit.</param>
        private IEnumerable<TokenString> GetMutableTokens(IDictionary<string, TokenString> records, IDictionary<string, IDictionary<int, TokenString>> fields)
        {
            if (records != null)
            {
                foreach (TokenString str in records.Values)
                {
                    if (str.Tokens.Any())
                        yield return str;
                }
            }

            if (fields != null)
            {
                foreach (TokenString str in fields.SelectMany(p => p.Value.Values))
                {
                    if (str.Tokens.Any())
                        yield return str;
                }
            }
        }

        /// <summary>Apply the patch to an asset.</summary>
        /// <typeparam name="TKey">The dictionary key type.</typeparam>
        /// <param name="asset">The asset to edit.</param>
        private void ApplyImpl<TKey>(IAssetData asset)
        {
            IDictionary<TKey, string> data = asset.AsDictionary<TKey, string>().Data;

            // apply records
            if (this.Records != null)
            {
                foreach (KeyValuePair<string, TokenString> record in this.Records)
                {
                    TKey key = (TKey)Convert.ChangeType(record.Key, typeof(TKey));
                    if (record.Value != null)
                        data[key] = record.Value.Value;
                    else
                        data.Remove(key);
                }
            }

            // apply fields
            if (this.Fields != null)
            {
                foreach (KeyValuePair<string, IDictionary<int, TokenString>> record in this.Fields)
                {
                    TKey key = (TKey)Convert.ChangeType(record.Key, typeof(TKey));
                    if (!data.ContainsKey(key))
                    {
                        this.Monitor.Log($"Can't apply data patch \"{this.LogName}\" to {this.AssetName}: there's no record matching key '{key}' under {nameof(PatchConfig.Fields)}.", LogLevel.Warn);
                        continue;
                    }

                    string[] actualFields = data[key].Split('/');
                    foreach (KeyValuePair<int, TokenString> field in record.Value)
                    {
                        if (field.Key < 0 || field.Key > actualFields.Length - 1)
                        {
                            this.Monitor.Log($"Can't apply data field \"{this.LogName}\" to {this.AssetName}: record '{key}' under {nameof(PatchConfig.Fields)} has no field with index {field.Key} (must be 0 to {actualFields.Length - 1}).", LogLevel.Warn);
                            continue;
                        }

                        actualFields[field.Key] = field.Value.Value;
                    }

                    data[key] = string.Join("/", actualFields);
                }
            }
        }
    }
}
