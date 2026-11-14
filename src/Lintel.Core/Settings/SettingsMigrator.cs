// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace Lintel.Core.Settings;

public static class SettingsMigrator
{
    public const int CurrentSchema = 1;

    /// <summary>Returns the root at the current schema. Schema 0 = a flat object (with or without "schema": 0), wrapped into "values".</summary>
    public static JsonObject Migrate(JsonObject root)
    {
        var schema = root["schema"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
        if (schema == 0)
        {
            root.Remove("schema");
            return new JsonObject { ["schema"] = CurrentSchema, ["values"] = root };
        }
        if (root["values"] is not JsonObject)
            root["values"] = new JsonObject();
        return root;
    }
}
