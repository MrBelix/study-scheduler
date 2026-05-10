using System.Text.RegularExpressions;

namespace StudyScheduler.Bot.Core.Routing;

public sealed record CallbackTemplate(string RouteKey, IReadOnlyList<string> ParamNames)
{
    private static readonly Regex ParamRegex = new(@"\{(\w+)\}", RegexOptions.Compiled);

    public static CallbackTemplate Parse(string template)
    {
        // student:view:{id} → routeKey="student:view", paramNames=["id"]
        var firstBrace = template.IndexOf('{');

        if (firstBrace == -1)
            return new(template, []);

        // routeKey — все до останнього ':' перед першим '{'
        var routeKey = template[..(firstBrace - 1)];
        
        var paramNames = ParamRegex.Matches(template)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        return new(routeKey, paramNames);
    }
}