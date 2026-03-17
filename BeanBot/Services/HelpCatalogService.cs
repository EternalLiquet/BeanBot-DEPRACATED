using NetCord.Services.Commands;
using System.ComponentModel;
using System.Reflection;

namespace BeanBot.Services;

public sealed class HelpCatalogService
{
    private readonly NullabilityInfoContext _nullabilityInfoContext = new();
    private readonly IReadOnlyList<HelpModuleDescriptor> _modules;
    private readonly IReadOnlyDictionary<string, HelpModuleDescriptor> _moduleLookup;
    private readonly IReadOnlyDictionary<string, HelpCommandDescriptor> _commandLookup;

    public HelpCatalogService()
    {
        _modules = BuildModules();

        _moduleLookup = _modules
            .SelectMany(module => module.LookupKeys.Select(key => new KeyValuePair<string, HelpModuleDescriptor>(key, module)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        _commandLookup = _modules
            .SelectMany(module => module.Commands)
            .SelectMany(command => command.LookupKeys.Select(key => new KeyValuePair<string, HelpCommandDescriptor>(key, command)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<HelpModuleDescriptor> GetModules()
        => _modules;

    public bool TryGetModule(string query, out HelpModuleDescriptor module)
        => _moduleLookup.TryGetValue(query.Trim(), out module!);

    public bool TryGetCommand(string query, out HelpCommandDescriptor command)
        => _commandLookup.TryGetValue(query.Trim(), out command!);

    private IReadOnlyList<HelpModuleDescriptor> BuildModules()
    {
        var moduleBuilders = new Dictionary<string, HelpModuleBuilder>(StringComparer.OrdinalIgnoreCase);

        var moduleTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(type =>
                !type.IsAbstract &&
                type.IsSubclassOf(typeof(CommandModule<CommandContext>)))
            .OrderBy(GetModuleOrder)
            .ThenBy(type => type.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var moduleType in moduleTypes)
        {
            var moduleName = moduleType.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                ?? TrimModuleSuffix(moduleType.Name);
            var moduleSummary = moduleType.GetCustomAttribute<DescriptionAttribute>()?.Description
                ?? "No module description has been written yet.";
            var moduleLookupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                moduleName,
                TrimModuleSuffix(moduleType.Name),
            };

            if (!moduleBuilders.TryGetValue(moduleName, out var moduleBuilder))
            {
                moduleBuilder = new HelpModuleBuilder(moduleName, moduleSummary, moduleLookupKeys);
                moduleBuilders[moduleName] = moduleBuilder;
            }

            foreach (var moduleLookupKey in moduleLookupKeys)
            {
                moduleBuilder.LookupKeys.Add(moduleLookupKey);
            }

            if (string.IsNullOrWhiteSpace(moduleBuilder.Summary) && !string.IsNullOrWhiteSpace(moduleSummary))
            {
                moduleBuilder.Summary = moduleSummary;
            }

            foreach (var method in moduleType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var commandAttribute = method.GetCustomAttribute<CommandAttribute>();
                if (commandAttribute is null)
                {
                    continue;
                }

                moduleBuilder.Commands.Add(BuildCommandDescriptor(moduleName, method, commandAttribute));
            }
        }

        return moduleBuilders.Values
            .OrderBy(builder => GetModuleNameOrder(builder.Name))
            .ThenBy(builder => builder.Name, StringComparer.OrdinalIgnoreCase)
            .Select(builder => new HelpModuleDescriptor(
                builder.Name,
                builder.Summary,
                builder.LookupKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                builder.Commands.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    private HelpCommandDescriptor BuildCommandDescriptor(string moduleName, MethodInfo method, CommandAttribute commandAttribute)
    {
        var aliases = commandAttribute.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var primaryName = aliases.FirstOrDefault() ?? method.Name;
        var additionalAliases = aliases.Skip(1).ToArray();
        var parameters = method.GetParameters()
            .Select(BuildParameterDescriptor)
            .ToArray();

        return new HelpCommandDescriptor(
            ModuleName: moduleName,
            Name: primaryName,
            Summary: method.GetCustomAttribute<DescriptionAttribute>()?.Description
                ?? "No command description has been written yet.",
            Usage: BuildUsage(primaryName, parameters),
            Aliases: additionalAliases,
            Parameters: parameters,
            GuildOnly: IsGuildOnly(method),
            LookupKeys: aliases);
    }

    private HelpParameterDescriptor BuildParameterDescriptor(ParameterInfo parameter)
    {
        var commandParameterAttribute = parameter.GetCustomAttribute<CommandParameterAttribute>();
        var parameterName = commandParameterAttribute?.Name ?? parameter.Name ?? "value";
        var isOptional = parameter.HasDefaultValue ||
                         _nullabilityInfoContext.Create(parameter).WriteState == NullabilityState.Nullable;

        return new HelpParameterDescriptor(
            parameterName,
            isOptional,
            commandParameterAttribute?.Remainder ?? false);
    }

    private static string BuildUsage(string commandName, IReadOnlyList<HelpParameterDescriptor> parameters)
    {
        if (parameters.Count == 0)
        {
            return $"%{commandName}";
        }

        var parameterSegments = parameters.Select(parameter =>
        {
            var wrappedName = parameter.IsOptional
                ? $"[{parameter.Name}]"
                : $"<{parameter.Name}>";
            return parameter.IsRemainder
                ? $"{wrappedName}..."
                : wrappedName;
        });

        return $"%{commandName} {string.Join(' ', parameterSegments)}";
    }

    private static bool IsGuildOnly(MethodInfo method)
        => method.CustomAttributes.Any(attribute =>
            attribute.AttributeType.Name.StartsWith("RequireContextAttribute", StringComparison.Ordinal) &&
            attribute.ConstructorArguments.Count > 0 &&
            string.Equals(attribute.ConstructorArguments[0].Value?.ToString(), "Guild", StringComparison.OrdinalIgnoreCase));

    private static string TrimModuleSuffix(string typeName)
        => typeName.EndsWith("Module", StringComparison.Ordinal)
            ? typeName[..^"Module".Length]
            : typeName;

    private static int GetModuleOrder(Type moduleType)
        => GetModuleNameOrder(moduleType.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? TrimModuleSuffix(moduleType.Name));

    private static int GetModuleNameOrder(string moduleName)
        => moduleName switch
        {
            "General" => 0,
            "Fun" => 1,
            "Administration" => 2,
            _ => 10,
        };

    private sealed class HelpModuleBuilder(string name, string summary, HashSet<string> lookupKeys)
    {
        public string Name { get; } = name;
        public string Summary { get; set; } = summary;
        public HashSet<string> LookupKeys { get; } = lookupKeys;
        public List<HelpCommandDescriptor> Commands { get; } = [];
    }
}

public sealed record HelpModuleDescriptor(
    string Name,
    string Summary,
    IReadOnlyList<string> LookupKeys,
    IReadOnlyList<HelpCommandDescriptor> Commands);

public sealed record HelpCommandDescriptor(
    string ModuleName,
    string Name,
    string Summary,
    string Usage,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<HelpParameterDescriptor> Parameters,
    bool GuildOnly,
    IReadOnlyList<string> LookupKeys);

public sealed record HelpParameterDescriptor(
    string Name,
    bool IsOptional,
    bool IsRemainder);
