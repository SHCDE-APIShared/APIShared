using APIShared.Internal;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared.ModSettings
{
    internal sealed partial class LobbyPresetController
    {
        // Read-only: never apply a working snapshot or refresh access/persistence for logging.
        // Use the controller's existing accessors and activation discovery, including dynamic schemas.
        internal Dictionary<string, object> CaptureRuntimeLogSettings(Action<string, Exception> warning)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            var errors = new List<object>();
            var read = new Dictionary<string, object>(StringComparer.Ordinal);
            var failed = new HashSet<string>(StringComparer.Ordinal);
            var backend = owner.SettingsApplicationBackend;
            Dictionary<string, object> applied = null;

            string Scope(PresetPropertyAccessor property) => IsHostProperty(property) ? "host" :
                property.GetCustomAttribute<SyncPerPlayerAttribute>() != null ? "player" : "local";
            void Error(string scope, string key, Exception exception)
            {
                var error = new Dictionary<string, object> { ["scope"] = scope, ["code"] = "read_failed" };
                if (key != null) error["key"] = key;
                errors.Add(error);
                try { warning?.Invoke(scope + (key == null ? "" : "." + key), exception); } catch { }
            }
            bool TryRead(PresetPropertyAccessor property, out object value)
            {
                value = null;
                if (property == null || failed.Contains(property.Name)) return false;
                if (read.TryGetValue(property.Name, out value)) return true;
                try
                {
                    if (backend != null)
                    {
                        if (!applied.TryGetValue(property.Name, out value))
                            throw new InvalidOperationException("Active configuration is missing setting " + property.Name);
                    }
                    else value = property.GetValue(owner.SettingsTarget);
                    // Detach mutable values and validate each value independently with our normal
                    // JSON codec. A bad getter, cycle or non-finite number cannot lose another setting.
                    // The final row and scope are two enclosing JSON objects.
                    value = DependencyFreeJson.CaptureValue(value, depth: 2);
                    read.Add(property.Name, value);
                    return true;
                }
                catch (Exception exception)
                {
                    failed.Add(property.Name);
                    Error(Scope(property), property.Name, exception);
                    return false;
                }
            }

            if (backend != null)
            {
                try { applied = backend.ReadActiveValues() ?? throw new InvalidOperationException("Active configuration is null."); }
                catch (Exception exception)
                {
                    Error("mod", null, exception);
                    result["errors"] = errors;
                    return result;
                }
            }
            bool Disabled(PresetPropertyAccessor activation) =>
                TryRead(activation, out object value) && value is bool enabled && !enabled;
            bool hostDisabled = Disabled(hostSettingsActivationProperty);
            bool clientDisabled = Disabled(clientSettingsActivationProperty);
            var disabled = new List<string>();
            foreach (string scope in new[] { "host", "player", "local" })
            {
                var properties = persistedProperties.Where(property => Scope(property) == scope)
                    .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                if (properties.Length == 0) continue;
                bool inactive = scope == "host" ? hostDisabled : clientDisabled;
                if (inactive) disabled.Add(scope);
                var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var property in properties)
                {
                    if (inactive && !ReferenceEquals(property, scope == "host" ? hostSettingsActivationProperty : clientSettingsActivationProperty))
                        continue;
                    if (TryRead(property, out object value)) values.Add(property.Name, value);
                }
                result[scope] = values;
            }
            if (disabled.Count != 0) result["disabled"] = disabled;
            if (errors.Count != 0) result["errors"] = errors;
            return result;
        }
    }

    public abstract partial class PresetLobbyModSettingsViewModel
    {
        internal Dictionary<string, object> CaptureRuntimeLogSettings(Action<string, Exception> warning) =>
            (presetController ?? throw new InvalidOperationException("Presets have not been prepared."))
                .CaptureRuntimeLogSettings(warning);
    }
}
