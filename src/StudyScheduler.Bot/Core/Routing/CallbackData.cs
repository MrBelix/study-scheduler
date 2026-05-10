using System.Globalization;

namespace StudyScheduler.Bot.Core.Routing;

public readonly record struct CallbackData
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public string RouteKey { get; }

    private CallbackData(string routeKey, IReadOnlyDictionary<string, string> values)
    {
        RouteKey = routeKey;
        _values = values;
    }

    public static CallbackData Parse(string raw, CallbackTemplate template)
    {
        // raw: "student:view:a1b2c3"
        // template.RouteKey: "student:view", template.ParamNames: ["id"]

        if (template.ParamNames.Count == 0)
            return new(template.RouteKey, new Dictionary<string, string>());

        var paramSection = raw[(template.RouteKey.Length + 1)..]; // "a1b2c3"
        var values = paramSection.Split(',');

        if (values.Length != template.ParamNames.Count)
            throw new ArgumentException(
                $"Очікувалось {template.ParamNames.Count} параметрів, отримано {values.Length}: '{raw}'");

        var dict = new Dictionary<string, string>(values.Length);
        for (var i = 0; i < values.Length; i++)
            dict[template.ParamNames[i]] = values[i];

        return new(template.RouteKey, dict);
    }

    public T Get<T>(string name) where T : IParsable<T>
    {
        if (!_values.TryGetValue(name, out var raw))
            throw new KeyNotFoundException($"Параметр '{name}' не знайдено");

        return T.Parse(raw, CultureInfo.InvariantCulture);
    }

    public string GetString(string name) =>
        _values.TryGetValue(name, out var v) 
            ? v 
            : throw new KeyNotFoundException($"Параметр '{name}' не знайдено");
}