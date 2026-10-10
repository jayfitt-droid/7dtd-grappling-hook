using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

internal sealed class HookSettings
{
    // Farthest impact that will attach, per hook tier (scrap, iron, steel).
    public float[] MaxDistance = { 15f, 25f, 35f };
    public float ReelInSpeed = 6f;
    public float ReelOutSpeed = 4f;
    public float BlockDamage = 10f;
    public bool FallDamageOnRelease = true;
    // Stamina per second while attached and off the ground; 0 turns it off.
    public float StaminaUse = 3f;

    public static HookSettings Load(string modDirectory)
    {
        var result = new HookSettings();
        string path = Path.Combine(modDirectory, "Config", "GrapplingHookConfig.json");
        if (!File.Exists(path))
        {
            Log.Warning("[GrapplingHook] Config missing; using defaults: " + path);
            return result;
        }
        try
        {
            JObject data = JObject.Parse(File.ReadAllText(path));
            result.MaxDistance[0] = Number(data, "scrap_max_distance", 15f, 2f, 200f);
            result.MaxDistance[1] = Number(data, "iron_max_distance", 25f, 2f, 200f);
            result.MaxDistance[2] = Number(data, "steel_max_distance", 35f, 2f, 200f);
            result.ReelInSpeed = Number(data, "reel_in_speed", 6f, 0.1f, 50f);
            result.ReelOutSpeed = Number(data, "reel_out_speed", 4f, 0.1f, 50f);
            result.BlockDamage = Number(data, "block_damage", 10f, 0f, 10000f);
            result.FallDamageOnRelease = Boolean(data, "fall_damage_on_release", true);
            result.StaminaUse = Number(data, "stamina_use", 3f, 0f, 100f);
            // Range is per tier now, controls are the game's own attack, aim and
            // jump actions, and the swing feel is fixed; old settings in an
            // existing config file are ignored.
            foreach (string old in new[] { "max_distance", "reel_in_key", "reel_out_key", "disconnect_key",
                "swing_force", "swing_damping" })
                if (data[old] != null)
                    Log.Out("[GrapplingHook] Config: " + old + " is no longer used and is ignored.");
            Log.Out("[GrapplingHook] Config loaded from " + path);
        }
        catch (Exception ex)
        {
            Log.Warning("[GrapplingHook] Invalid config; using defaults: " + ex.Message);
            return new HookSettings();
        }
        return result;
    }

    private static float Number(JObject data, string key, float fallback, float min, float max)
    {
        JToken token = data[key];
        if (token == null) return fallback;
        float value;
        if ((token.Type == JTokenType.Float || token.Type == JTokenType.Integer) &&
            float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max)
            return value;
        Log.Warning("[GrapplingHook] Invalid " + key + " (allowed " + min + " to " + max + "); using " + fallback);
        return fallback;
    }

    private static bool Boolean(JObject data, string key, bool fallback)
    {
        JToken token = data[key];
        if (token == null) return fallback;
        if (token.Type == JTokenType.Boolean) return token.Value<bool>();
        Log.Warning("[GrapplingHook] Invalid " + key + "; using " + fallback);
        return fallback;
    }
}
