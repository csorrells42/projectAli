using System.Text;
using System.Text.RegularExpressions;

namespace Ali.Infrastructure.Coding;

public sealed partial class LocalCodingToolService
{
    private static bool UsesLegacyStarterRecipeLane(string goal) =>
        MentionsAny(goal, "starter", "template", "scaffold", "scaffolding", "recipe", "sample app", "sample project", "boilerplate");

    private static bool TryBuildSimpleConsoleProgramCreateBlock(
        string goal,
        string fullPath,
        out string newText,
        out string note)
    {
        newText = string.Empty;
        note = string.Empty;
        if (!Path.GetFileName(fullPath).Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
            || !IsSimpleConsoleProgramGoal(goal))
        {
            return false;
        }

        newText = BuildSimpleConsoleProgramText(goal, Environment.NewLine, out note);
        note = "Create Program.cs. " + note;
        return true;
    }

    private static bool TryBuildConsoleProjectOutputTypePatchBlock(
        string content,
        string goal,
        string fullPath,
        out string oldText,
        out string newText,
        out string note)
    {
        oldText = string.Empty;
        newText = string.Empty;
        note = string.Empty;
        if (!fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || !IsSimpleConsoleProgramGoal(goal)
            || content.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var newline = GetPreferredNewline(content);
        var outputTypeMatch = Regex.Match(
            content,
            @"<OutputType>\s*[^<]+\s*</OutputType>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (outputTypeMatch.Success)
        {
            oldText = outputTypeMatch.Value;
            newText = "<OutputType>Exe</OutputType>";
            note = "Set project output type to executable.";
            return !oldText.Equals(newText, StringComparison.Ordinal);
        }

        var propertyGroupMatch = Regex.Match(
            content,
            @"<PropertyGroup>[ \t]*(?:\r?\n)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (propertyGroupMatch.Success
            && TryBuildUniqueContextBlock(content, propertyGroupMatch.Index, propertyGroupMatch.Length, out oldText))
        {
            var insertion = propertyGroupMatch.Value + "    <OutputType>Exe</OutputType>" + newline;
            newText = oldText.Replace(propertyGroupMatch.Value, insertion, StringComparison.Ordinal);
            note = "Add executable output type to the existing project property group.";
            return !oldText.Equals(newText, StringComparison.Ordinal);
        }

        var projectMatch = Regex.Match(
            content,
            @"<Project\b[^>]*>[ \t]*(?:\r?\n)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (projectMatch.Success
            && TryBuildUniqueContextBlock(content, projectMatch.Index, projectMatch.Length, out oldText))
        {
            var insertion = projectMatch.Value
                + "  <PropertyGroup>" + newline
                + "    <OutputType>Exe</OutputType>" + newline
                + "  </PropertyGroup>" + newline;
            newText = oldText.Replace(projectMatch.Value, insertion, StringComparison.Ordinal);
            note = "Add an executable property group to the project file.";
            return !oldText.Equals(newText, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool TryBuildSimpleConsoleProgramPatchBlock(
        string content,
        string goal,
        string fullPath,
        out string oldText,
        out string newText,
        out string note)
    {
        oldText = string.Empty;
        newText = string.Empty;
        note = string.Empty;
        if (!Path.GetFileName(fullPath).Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
            || !IsSimpleConsoleProgramGoal(goal))
        {
            return false;
        }

        var newline = GetPreferredNewline(content);
        oldText = content;
        newText = BuildSimpleConsoleProgramText(goal, newline, out note);
        return !content.Equals(newText, StringComparison.Ordinal);
    }

    private static string BuildSimpleConsoleProgramText(string goal, string newline, out string note)
    {
        var waitsForKey = MentionsAny(
            goal,
            "keypress",
            "key press",
            "press a key",
            "press any key",
            "press a button",
            "press button",
            "before closing",
            "before it closes",
            "waits for");
        var replacementLines = BuildSimpleConsoleProgramLines(goal, waitsForKey).ToList();

        note = ClassifySimpleConsoleProgramNote(goal, waitsForKey);
        return string.Join(newline, replacementLines) + newline;
    }

    private static IReadOnlyList<string> BuildSimpleConsoleProgramLines(string goal, bool waitsForKey)
    {
        var lines = IsConsoleGuessingGameGoal(goal)
            ? BuildGuessingGameConsoleProgramLines()
            : IsConsoleTodoListGoal(goal)
                ? BuildTodoListConsoleProgramLines()
                : IsConsoleNotesAppGoal(goal)
                    ? BuildNotesAppConsoleProgramLines()
                    : TryBuildGenericConsoleListProgramLines(goal, out var genericListLines)
                        ? genericListLines
                        : IsConsoleCalculatorGoal(goal)
                            ? BuildCalculatorConsoleProgramLines()
                            : IsConsoleAddTwoIntegersGoal(goal)
                                ? BuildAddTwoIntegersConsoleProgramLines()
                                : IsConsoleFactorialGoal(goal)
                                    ? BuildFactorialConsoleProgramLines()
                                    : BuildHelloWorldConsoleProgramLines();
        return waitsForKey
            ? lines.Concat(
                [
                    string.Empty,
                    "Console.WriteLine(\"Press any key to exit...\");",
                    "Console.ReadKey(intercept: true);"
                ]).ToList()
            : lines;
    }

    private static bool TryBuildGenericConsoleListProgramLines(string goal, out IReadOnlyList<string> lines)
    {
        lines = [];
        if (!IsGenericConsoleListManagerGoal(goal))
        {
            return false;
        }

        var itemName = InferConsoleListItemName(goal);
        var recipe = new ConsoleListRecipe(
            BuildConsoleListTitle(goal, itemName),
            itemName,
            PluralizeIdentifier(itemName),
            MentionsAny(goal, "file", "save", "saved", "persist", "load", "storage"));
        lines = recipe.FileBacked
            ? BuildFileBackedConsoleListProgramLines(recipe)
            : BuildInMemoryConsoleListProgramLines(recipe);
        return true;
    }

    private static IReadOnlyList<string> BuildInMemoryConsoleListProgramLines(ConsoleListRecipe recipe)
    {
        var itemTitle = CapitalizeInvariant(recipe.ItemName);
        return
        [
            $"var {recipe.VariableName} = new List<string>();",
            string.Empty,
            "while (true)",
            "{",
            "    Console.WriteLine();",
            $"    Console.WriteLine(\"{recipe.Title}\");",
            $"    Console.WriteLine(\"1. Add {recipe.ItemName}\");",
            $"    Console.WriteLine(\"2. List {recipe.VariableName}\");",
            $"    Console.WriteLine(\"3. Remove {recipe.ItemName}\");",
            "    Console.WriteLine(\"4. Quit\");",
            "    Console.Write(\"Choose an option: \");",
            "    var choice = Console.ReadLine();",
            string.Empty,
            "    if (choice == \"1\")",
            "    {",
            $"        Console.Write(\"{itemTitle}: \");",
            $"        var {recipe.ItemName} = Console.ReadLine();",
            $"        if (!string.IsNullOrWhiteSpace({recipe.ItemName}))",
            "        {",
            $"            {recipe.VariableName}.Add({recipe.ItemName}.Trim());",
            $"            Console.WriteLine(\"{itemTitle} added.\");",
            "        }",
            "    }",
            "    else if (choice == \"2\")",
            "    {",
            $"        if ({recipe.VariableName}.Count == 0)",
            "        {",
            $"            Console.WriteLine(\"No {recipe.VariableName} yet.\");",
            "        }",
            "        else",
            "        {",
            $"            for (var index = 0; index < {recipe.VariableName}.Count; index++)",
            "            {",
            $"                Console.WriteLine($\"{{index + 1}}. {{{recipe.VariableName}[index]}}\");",
            "            }",
            "        }",
            "    }",
            "    else if (choice == \"3\")",
            "    {",
            $"        Console.Write(\"{itemTitle} number to remove: \");",
            $"        if (int.TryParse(Console.ReadLine(), out var itemNumber) && itemNumber >= 1 && itemNumber <= {recipe.VariableName}.Count)",
            "        {",
            $"            {recipe.VariableName}.RemoveAt(itemNumber - 1);",
            $"            Console.WriteLine(\"{itemTitle} removed.\");",
            "        }",
            "        else",
            "        {",
            $"            Console.WriteLine(\"That {recipe.ItemName} number was not found.\");",
            "        }",
            "    }",
            "    else if (choice == \"4\")",
            "    {",
            "        break;",
            "    }",
            "    else",
            "    {",
            "        Console.WriteLine(\"Please choose 1, 2, 3, or 4.\");",
            "    }",
            "}"
        ];
    }

    private static IReadOnlyList<string> BuildFileBackedConsoleListProgramLines(ConsoleListRecipe recipe)
    {
        var itemTitle = CapitalizeInvariant(recipe.ItemName);
        var fileName = $"{recipe.VariableName}.txt";
        return
        [
            $"const string storagePath = \"{fileName}\";",
            $"var {recipe.VariableName} = System.IO.File.Exists(storagePath) ? new List<string>(System.IO.File.ReadAllLines(storagePath)) : new List<string>();",
            string.Empty,
            "while (true)",
            "{",
            "    Console.WriteLine();",
            $"    Console.WriteLine(\"{recipe.Title}\");",
            $"    Console.WriteLine(\"1. Add {recipe.ItemName}\");",
            $"    Console.WriteLine(\"2. List {recipe.VariableName}\");",
            $"    Console.WriteLine(\"3. Remove {recipe.ItemName}\");",
            "    Console.WriteLine(\"4. Quit\");",
            "    Console.Write(\"Choose an option: \");",
            "    var choice = Console.ReadLine();",
            string.Empty,
            "    if (choice == \"1\")",
            "    {",
            $"        Console.Write(\"{itemTitle}: \");",
            $"        var {recipe.ItemName} = Console.ReadLine();",
            $"        if (!string.IsNullOrWhiteSpace({recipe.ItemName}))",
            "        {",
            $"            {recipe.VariableName}.Add({recipe.ItemName}.Trim());",
            $"            System.IO.File.AppendAllText(storagePath, {recipe.ItemName}.Trim() + Environment.NewLine);",
            $"            Console.WriteLine(\"{itemTitle} saved.\");",
            "        }",
            "    }",
            "    else if (choice == \"2\")",
            "    {",
            $"        {recipe.VariableName} = System.IO.File.Exists(storagePath) ? new List<string>(System.IO.File.ReadAllLines(storagePath)) : new List<string>();",
            $"        if ({recipe.VariableName}.Count == 0)",
            "        {",
            $"            Console.WriteLine(\"No {recipe.VariableName} saved yet.\");",
            "        }",
            "        else",
            "        {",
            $"            for (var index = 0; index < {recipe.VariableName}.Count; index++)",
            "            {",
            $"                Console.WriteLine($\"{{index + 1}}. {{{recipe.VariableName}[index]}}\");",
            "            }",
            "        }",
            "    }",
            "    else if (choice == \"3\")",
            "    {",
            $"        Console.Write(\"{itemTitle} number to remove: \");",
            $"        if (int.TryParse(Console.ReadLine(), out var itemNumber) && itemNumber >= 1 && itemNumber <= {recipe.VariableName}.Count)",
            "        {",
            $"            {recipe.VariableName}.RemoveAt(itemNumber - 1);",
            $"            System.IO.File.WriteAllLines(storagePath, {recipe.VariableName});",
            $"            Console.WriteLine(\"{itemTitle} removed.\");",
            "        }",
            "        else",
            "        {",
            $"            Console.WriteLine(\"That {recipe.ItemName} number was not found.\");",
            "        }",
            "    }",
            "    else if (choice == \"4\")",
            "    {",
            "        break;",
            "    }",
            "    else",
            "    {",
            "        Console.WriteLine(\"Please choose 1, 2, 3, or 4.\");",
            "    }",
            "}"
        ];
    }

    private static string InferConsoleListItemName(string goal)
    {
        if (MentionsAny(goal, "contact", "contacts", "address book"))
        {
            return "contact";
        }

        if (MentionsAny(goal, "book", "books", "library"))
        {
            return "book";
        }

        if (MentionsAny(goal, "recipe", "recipes"))
        {
            return "recipe";
        }

        if (MentionsAny(goal, "movie", "movies", "watch list"))
        {
            return "movie";
        }

        if (MentionsAny(goal, "task", "tasks", "todo", "to-do"))
        {
            return "task";
        }

        return "item";
    }

    private static string BuildConsoleListTitle(string goal, string itemName)
    {
        if (MentionsAny(goal, "shopping", "grocery"))
        {
            return "Shopping List";
        }

        if (MentionsAny(goal, "inventory"))
        {
            return "Inventory";
        }

        if (MentionsAny(goal, "address book", "contact", "contacts"))
        {
            return "Contact Manager";
        }

        if (MentionsAny(goal, "library"))
        {
            return "Library";
        }

        return $"{CapitalizeInvariant(itemName)} Manager";
    }

    private static string PluralizeIdentifier(string itemName) =>
        itemName.EndsWith("y", StringComparison.OrdinalIgnoreCase)
            ? itemName[..^1] + "ies"
            : itemName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                ? itemName
                : itemName + "s";

    private static string CapitalizeInvariant(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Item"
            : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record ConsoleListRecipe(string Title, string ItemName, string VariableName, bool FileBacked);

    private static IReadOnlyList<string> BuildHelloWorldConsoleProgramLines() =>
        ["Console.WriteLine(\"Hello, World!\");"];

    private static IReadOnlyList<string> BuildAddTwoIntegersConsoleProgramLines() =>
    [
        "Console.Write(\"Enter the first integer: \");",
        "var firstInput = Console.ReadLine();",
        string.Empty,
        "Console.Write(\"Enter the second integer: \");",
        "var secondInput = Console.ReadLine();",
        string.Empty,
        "if (!int.TryParse(firstInput, out var firstNumber) || !int.TryParse(secondInput, out var secondNumber))",
        "{",
        "    Console.WriteLine(\"Please enter valid whole numbers.\");",
        "}",
        "else",
        "{",
        "    var sum = firstNumber + secondNumber;",
        "    Console.WriteLine($\"{firstNumber} + {secondNumber} = {sum}\");",
        "}"
    ];

    private static IReadOnlyList<string> BuildCalculatorConsoleProgramLines() =>
    [
        "Console.Write(\"Enter the first number: \");",
        "var firstInput = Console.ReadLine();",
        string.Empty,
        "Console.Write(\"Enter an operator (+, -, *, /): \");",
        "var operation = Console.ReadLine()?.Trim();",
        string.Empty,
        "Console.Write(\"Enter the second number: \");",
        "var secondInput = Console.ReadLine();",
        string.Empty,
        "if (!double.TryParse(firstInput, out var firstNumber) || !double.TryParse(secondInput, out var secondNumber))",
        "{",
        "    Console.WriteLine(\"Please enter valid numbers.\");",
        "}",
        "else",
        "{",
        "    var resultText = operation switch",
        "    {",
        "        \"+\" => $\"{firstNumber} + {secondNumber} = {firstNumber + secondNumber}\",",
        "        \"-\" => $\"{firstNumber} - {secondNumber} = {firstNumber - secondNumber}\",",
        "        \"*\" => $\"{firstNumber} * {secondNumber} = {firstNumber * secondNumber}\",",
        "        \"/\" when secondNumber != 0 => $\"{firstNumber} / {secondNumber} = {firstNumber / secondNumber}\",",
        "        \"/\" => \"Cannot divide by zero.\",",
        "        _ => \"Unknown operator. Please use +, -, *, or /.\"",
        "    };",
        string.Empty,
        "    Console.WriteLine(resultText);",
        "}"
    ];

    private static IReadOnlyList<string> BuildGuessingGameConsoleProgramLines() =>
    [
        "var targetNumber = Random.Shared.Next(1, 101);",
        "var attempts = 0;",
        string.Empty,
        "Console.WriteLine(\"I picked a number between 1 and 100.\");",
        string.Empty,
        "while (true)",
        "{",
        "    Console.Write(\"Enter your guess: \");",
        "    var input = Console.ReadLine();",
        "    if (!int.TryParse(input, out var guess))",
        "    {",
        "        Console.WriteLine(\"Please enter a whole number.\");",
        "        continue;",
        "    }",
        string.Empty,
        "    attempts++;",
        "    if (guess < targetNumber)",
        "    {",
        "        Console.WriteLine(\"Too low.\");",
        "    }",
        "    else if (guess > targetNumber)",
        "    {",
        "        Console.WriteLine(\"Too high.\");",
        "    }",
        "    else",
        "    {",
        "        Console.WriteLine($\"Correct! You guessed it in {attempts} attempt(s).\");",
        "        break;",
        "    }",
        "}"
    ];

    private static IReadOnlyList<string> BuildTodoListConsoleProgramLines() =>
    [
        "var tasks = new List<string>();",
        string.Empty,
        "while (true)",
        "{",
        "    Console.WriteLine();",
        "    Console.WriteLine(\"Todo List\");",
        "    Console.WriteLine(\"1. Add task\");",
        "    Console.WriteLine(\"2. List tasks\");",
        "    Console.WriteLine(\"3. Remove task\");",
        "    Console.WriteLine(\"4. Quit\");",
        "    Console.Write(\"Choose an option: \");",
        "    var choice = Console.ReadLine();",
        string.Empty,
        "    if (choice == \"1\")",
        "    {",
        "        Console.Write(\"Task: \");",
        "        var task = Console.ReadLine();",
        "        if (!string.IsNullOrWhiteSpace(task))",
        "        {",
        "            tasks.Add(task.Trim());",
        "            Console.WriteLine(\"Task added.\");",
        "        }",
        "    }",
        "    else if (choice == \"2\")",
        "    {",
        "        if (tasks.Count == 0)",
        "        {",
        "            Console.WriteLine(\"No tasks yet.\");",
        "        }",
        "        else",
        "        {",
        "            for (var index = 0; index < tasks.Count; index++)",
        "            {",
        "                Console.WriteLine($\"{index + 1}. {tasks[index]}\");",
        "            }",
        "        }",
        "    }",
        "    else if (choice == \"3\")",
        "    {",
        "        Console.Write(\"Task number to remove: \");",
        "        if (int.TryParse(Console.ReadLine(), out var taskNumber) && taskNumber >= 1 && taskNumber <= tasks.Count)",
        "        {",
        "            tasks.RemoveAt(taskNumber - 1);",
        "            Console.WriteLine(\"Task removed.\");",
        "        }",
        "        else",
        "        {",
        "            Console.WriteLine(\"That task number was not found.\");",
        "        }",
        "    }",
        "    else if (choice == \"4\")",
        "    {",
        "        break;",
        "    }",
        "    else",
        "    {",
        "        Console.WriteLine(\"Please choose 1, 2, 3, or 4.\");",
        "    }",
        "}"
    ];

    private static IReadOnlyList<string> BuildNotesAppConsoleProgramLines() =>
    [
        "const string notesPath = \"notes.txt\";",
        string.Empty,
        "while (true)",
        "{",
        "    Console.WriteLine();",
        "    Console.WriteLine(\"Notes\");",
        "    Console.WriteLine(\"1. Add note\");",
        "    Console.WriteLine(\"2. List notes\");",
        "    Console.WriteLine(\"3. Clear notes\");",
        "    Console.WriteLine(\"4. Quit\");",
        "    Console.Write(\"Choose an option: \");",
        "    var choice = Console.ReadLine();",
        string.Empty,
        "    if (choice == \"1\")",
        "    {",
        "        Console.Write(\"Note: \");",
        "        var note = Console.ReadLine();",
        "        if (!string.IsNullOrWhiteSpace(note))",
        "        {",
        "            System.IO.File.AppendAllText(notesPath, note.Trim() + Environment.NewLine);",
        "            Console.WriteLine(\"Note saved.\");",
        "        }",
        "    }",
        "    else if (choice == \"2\")",
        "    {",
        "        if (!System.IO.File.Exists(notesPath) || new System.IO.FileInfo(notesPath).Length == 0)",
        "        {",
        "            Console.WriteLine(\"No notes saved yet.\");",
        "        }",
        "        else",
        "        {",
        "            var notes = System.IO.File.ReadAllLines(notesPath);",
        "            for (var index = 0; index < notes.Length; index++)",
        "            {",
        "                Console.WriteLine($\"{index + 1}. {notes[index]}\");",
        "            }",
        "        }",
        "    }",
        "    else if (choice == \"3\")",
        "    {",
        "        System.IO.File.WriteAllText(notesPath, string.Empty);",
        "        Console.WriteLine(\"Notes cleared.\");",
        "    }",
        "    else if (choice == \"4\")",
        "    {",
        "        break;",
        "    }",
        "    else",
        "    {",
        "        Console.WriteLine(\"Please choose 1, 2, 3, or 4.\");",
        "    }",
        "}"
    ];

    private static IReadOnlyList<string> BuildFactorialConsoleProgramLines() =>
    [
        "Console.Write(\"Enter an integer between 1 and 9: \");",
        "var input = Console.ReadLine();",
        string.Empty,
        "if (!int.TryParse(input, out var number) || number < 1 || number > 9)",
        "{",
        "    Console.WriteLine(\"Please enter a whole number from 1 to 9.\");",
        "}",
        "else",
        "{",
        "    var factorial = 1;",
        "    for (var value = 2; value <= number; value++)",
        "    {",
        "        factorial *= value;",
        "    }",
        string.Empty,
        "    Console.WriteLine($\"{number}! = {factorial}\");",
        "}"
    ];

    private static string ClassifySimpleConsoleProgramNote(string goal, bool waitsForKey)
    {
        var shape = IsConsoleGuessingGameGoal(goal)
            ? "Console guessing-game starter recipe"
            : IsConsoleTodoListGoal(goal)
                ? "Console todo-list starter recipe"
                : IsConsoleNotesAppGoal(goal)
                    ? "Console notes-app starter recipe"
                    : IsGenericConsoleListManagerGoal(goal)
                        ? "Console list-manager starter recipe"
                        : IsConsoleCalculatorGoal(goal)
                            ? "Console calculator starter recipe"
                            : IsConsoleAddTwoIntegersGoal(goal)
                                ? "Console add-two-integers starter recipe"
                                : IsConsoleFactorialGoal(goal)
                                    ? "Console factorial starter recipe"
                                    : "Console hello-world starter recipe";
        return waitsForKey ? $"{shape} with a keypress hold before exit." : $"{shape}.";
    }

    private static string GetPreferredNewline(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static bool IsSimpleConsoleProgramGoal(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return false;
        }

        var isConsoleish = MentionsAny(
            goal,
            "console",
            "c#",
            "csharp",
            "program",
            "app",
            "application",
            "prints",
            "print",
            "says",
            "display",
            "write");
        return isConsoleish
               && (IsConsoleHelloWorldGoal(goal)
                   || IsConsoleGuessingGameGoal(goal)
                   || IsConsoleTodoListGoal(goal)
                   || IsConsoleNotesAppGoal(goal)
                   || IsGenericConsoleListManagerGoal(goal)
                   || IsConsoleCalculatorGoal(goal)
                   || IsConsoleAddTwoIntegersGoal(goal)
                   || IsConsoleFactorialGoal(goal));
    }

    private static bool IsConsoleHelloWorldGoal(string goal) =>
        goal.Contains("hello world", StringComparison.OrdinalIgnoreCase)
        || goal.Contains("hello-world", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsoleAddTwoIntegersGoal(string goal) =>
        MentionsAny(goal, "add", "sum", "adds", "together", "total")
        && MentionsAny(goal, "two", "2")
        && MentionsAny(goal, "integer", "integers", "number", "numbers", "whole number");

    private static bool IsConsoleFactorialGoal(string goal) =>
        goal.Contains("factorial", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsoleCalculatorGoal(string goal)
    {
        if (MentionsAny(goal, "calculator", "calculate", "arithmetic"))
        {
            return true;
        }

        var operationCount = 0;
        operationCount += MentionsAny(goal, "add", "sum", "plus") ? 1 : 0;
        operationCount += MentionsAny(goal, "subtract", "subtraction", "minus") ? 1 : 0;
        operationCount += MentionsAny(goal, "multiply", "multiplication", "times") ? 1 : 0;
        operationCount += MentionsAny(goal, "divide", "division") ? 1 : 0;

        return operationCount >= 2
               && MentionsAny(goal, "number", "numbers", "operator", "operation", "math");
    }

    private static bool IsConsoleGuessingGameGoal(string goal) =>
        MentionsAny(goal, "guessing game", "guess a number", "number guessing", "random number")
        || (MentionsAny(goal, "guess", "guesses") && MentionsAny(goal, "random", "too high", "too low"));

    private static bool IsConsoleTodoListGoal(string goal) =>
        MentionsAny(goal, "todo", "to-do", "task list", "tasks", "checklist")
        && MentionsAny(goal, "add", "list", "remove", "menu", "quit");

    private static bool IsConsoleNotesAppGoal(string goal) =>
        MentionsAny(goal, "notes app", "note taking", "take notes", "save notes", "notes")
        && MentionsAny(goal, "file", "save", "saved", "persist", "load", "list");

    private static bool IsGenericConsoleListManagerGoal(string goal) =>
        MentionsAny(goal, "list", "manager", "tracker", "address book", "inventory", "shopping", "grocery", "contacts", "contact", "books", "library", "recipes", "movies")
        && MentionsAny(goal, "add", "create", "list", "show", "remove", "delete", "clear", "save", "quit", "menu");

    private static bool TryBuildSimpleWpfPatchBlock(
        string content,
        string goal,
        string fullPath,
        out string oldText,
        out string newText,
        out string note)
    {
        oldText = string.Empty;
        newText = string.Empty;
        note = string.Empty;
        if (!IsSimpleWpfProgramGoal(goal))
        {
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        if (fileName.Equals("MainWindow.xaml", StringComparison.OrdinalIgnoreCase)
            && content.Contains("<Window", StringComparison.OrdinalIgnoreCase))
        {
            oldText = content;
            var xamlClass = ExtractXamlClassName(content) ?? "MainWindow";
            newText = BuildWpfWindowXaml(goal, xamlClass);
            note = ClassifyWpfStarterNote(goal, "MainWindow.xaml");
            return true;
        }

        if (fileName.Equals("MainWindow.xaml.cs", StringComparison.OrdinalIgnoreCase)
            && NeedsWpfCodeBehind(goal)
            && content.Contains("partial class MainWindow", StringComparison.OrdinalIgnoreCase))
        {
            oldText = content;
            newText = BuildWpfCodeBehind(goal, ExtractCSharpNamespaceName(content));
            note = ClassifyWpfStarterNote(goal, "MainWindow.xaml.cs");
            return true;
        }

        return false;
    }

    private static string BuildWpfWindowXaml(string goal, string xamlClass)
    {
        if (IsWpfComplexWindowGoal(goal))
        {
            return BuildWpfComplexDashboardWindowXaml(xamlClass);
        }

        if (IsWpfTodoGoal(goal))
        {
            return BuildWpfTodoWindowXaml(xamlClass);
        }

        if (IsWpfCalculatorGoal(goal))
        {
            return BuildWpfCalculatorWindowXaml(xamlClass);
        }

        if (IsWpfGreetingGoal(goal))
        {
            return BuildWpfGreetingWindowXaml(xamlClass);
        }

        return IsWpfCounterGoal(goal)
            ? BuildWpfCounterWindowXaml(xamlClass)
            : BuildWpfHelloWindowXaml(xamlClass);
    }

    private static string BuildWpfCodeBehind(string goal, string? namespaceName)
    {
        if (IsWpfComplexWindowGoal(goal))
        {
            return BuildWpfComplexDashboardCodeBehind(namespaceName);
        }

        if (IsWpfTodoGoal(goal))
        {
            return BuildWpfTodoCodeBehind(namespaceName);
        }

        if (IsWpfCalculatorGoal(goal))
        {
            return BuildWpfCalculatorCodeBehind(namespaceName);
        }

        if (IsWpfGreetingGoal(goal))
        {
            return BuildWpfGreetingCodeBehind(namespaceName);
        }

        return BuildWpfCounterCodeBehind(namespaceName);
    }

    private static string BuildWpfCounterWindowXaml(string xamlClass) =>
        $"""
        <Window x:Class="{xamlClass}"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="Counter" Height="320" Width="420">
            <Grid Margin="24">
                <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
                    <TextBlock Text="Counter"
                               FontSize="28"
                               FontWeight="SemiBold"
                               HorizontalAlignment="Center" />
                    <TextBlock x:Name="CounterTextBlock"
                               Text="Count: 0"
                               FontSize="22"
                               Margin="0,18,0,18"
                               HorizontalAlignment="Center" />
                    <Button Content="Add One"
                            Width="140"
                            Height="38"
                            Click="IncrementButton_Click" />
                </StackPanel>
            </Grid>
        </Window>
        """;

    private static string BuildWpfHelloWindowXaml(string xamlClass) =>
        $"""
        <Window x:Class="{xamlClass}"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="Hello" Height="280" Width="420">
            <Grid Margin="24">
                <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
                    <TextBlock Text="Hello, World!"
                               FontSize="30"
                               FontWeight="SemiBold"
                               HorizontalAlignment="Center" />
                    <TextBlock Text="Your WPF app is running."
                               FontSize="16"
                               Margin="0,12,0,0"
                               HorizontalAlignment="Center" />
                </StackPanel>
            </Grid>
        </Window>
        """;

    private static string BuildWpfCalculatorWindowXaml(string xamlClass) =>
        $"""
        <Window x:Class="{xamlClass}"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="Calculator" Height="360" Width="460">
            <Grid Margin="24">
                <StackPanel VerticalAlignment="Center">
                    <TextBlock Text="Calculator"
                               FontSize="28"
                               FontWeight="SemiBold"
                               Margin="0,0,0,16" />
                    <TextBox x:Name="FirstNumberTextBox"
                             Height="34"
                             Margin="0,0,0,10"
                             VerticalContentAlignment="Center" />
                    <ComboBox x:Name="OperationComboBox"
                              Height="34"
                              SelectedIndex="0"
                              Margin="0,0,0,10">
                        <ComboBoxItem Content="Add" />
                        <ComboBoxItem Content="Subtract" />
                        <ComboBoxItem Content="Multiply" />
                        <ComboBoxItem Content="Divide" />
                    </ComboBox>
                    <TextBox x:Name="SecondNumberTextBox"
                             Height="34"
                             Margin="0,0,0,16"
                             VerticalContentAlignment="Center" />
                    <Button Content="Calculate"
                            Height="38"
                            Click="CalculateButton_Click" />
                    <TextBlock x:Name="ResultTextBlock"
                               FontSize="18"
                               Margin="0,18,0,0" />
                </StackPanel>
            </Grid>
        </Window>
        """;

    private static string BuildWpfGreetingWindowXaml(string xamlClass) =>
        $"""
        <Window x:Class="{xamlClass}"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="Greeting" Height="300" Width="440">
            <Grid Margin="24">
                <StackPanel VerticalAlignment="Center">
                    <TextBlock Text="Greeting"
                               FontSize="28"
                               FontWeight="SemiBold"
                               Margin="0,0,0,16" />
                    <TextBox x:Name="NameTextBox"
                             Height="34"
                             Margin="0,0,0,12"
                             VerticalContentAlignment="Center" />
                    <Button Content="Say Hello"
                            Height="38"
                            Click="GreetButton_Click" />
                    <TextBlock x:Name="GreetingTextBlock"
                               FontSize="18"
                               Margin="0,18,0,0" />
                </StackPanel>
            </Grid>
        </Window>
        """;

    private static string BuildWpfComplexDashboardWindowXaml(string xamlClass) =>
        $$"""
        <Window x:Class="{{xamlClass}}"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                xmlns:diag="clr-namespace:System.Diagnostics;assembly=WindowsBase"
                xmlns:local="{{BuildWpfLocalXmlNamespace(xamlClass)}}"
                xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                Title="Project Dashboard" Height="620" Width="980" MinHeight="520" MinWidth="760"
                mc:Ignorable="d"
                d:DataContext="{x:Static local:DashboardDesignData.DesignViewModel}">
            <Window.Resources>
                <ResourceDictionary>
                    <ResourceDictionary.MergedDictionaries>
                        <ResourceDictionary Source="AliDashboardStyles.xaml" />
                    </ResourceDictionary.MergedDictionaries>
                    <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
                    <local:DashboardStatusBrushConverter x:Key="DashboardStatusBrushConverter" />
                    <local:DashboardSelectionSummaryConverter x:Key="DashboardSelectionSummaryConverter" />
                    <local:DashboardBindingProxy x:Key="DashboardCommandsProxy" Data="{Binding}" />
                    <DataTemplate x:Key="DashboardDetailTemplate">
                        <local:DashboardDetailCard Item="{Binding}" PromoteRequested="DashboardDetailCard_PromoteRequested" />
                    </DataTemplate>
                    <DataTemplate x:Key="ReadyDashboardItemCardTemplate">
                        <Border Padding="10" Background="#ECFDF3" BorderBrush="#17B26A" BorderThickness="1" CornerRadius="4">
                            <StackPanel>
                                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" />
                                <TextBlock Text="Ready for delivery" Foreground="#067647" />
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                    <DataTemplate x:Key="ReviewDashboardItemCardTemplate">
                        <Border Padding="10" Background="#FFFAEB" BorderBrush="#F79009" BorderThickness="1" CornerRadius="4">
                            <StackPanel>
                                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" />
                                <TextBlock Text="Review before release" Foreground="#B54708" />
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                    <DataTemplate x:Key="DefaultDashboardItemCardTemplate">
                        <Border Padding="10" Background="#F6F8FA" BorderBrush="#D0D7DE" BorderThickness="1" CornerRadius="4">
                            <StackPanel>
                                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" />
                                <TextBlock Text="{Binding Status}" />
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                    <local:DashboardItemCardTemplateSelector x:Key="DashboardItemCardTemplateSelector"
                                                             ReadyTemplate="{StaticResource ReadyDashboardItemCardTemplate}"
                                                             ReviewTemplate="{StaticResource ReviewDashboardItemCardTemplate}"
                                                             DefaultTemplate="{StaticResource DefaultDashboardItemCardTemplate}" />
                    <DataTemplate DataType="{x:Type local:OverviewDashboardViewModel}">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="*" />
                            </Grid.RowDefinitions>
                            <ItemsControl ItemsSource="{Binding Metrics}" Margin="0,0,0,10">
                                <ItemsControl.ItemsPanel>
                                    <ItemsPanelTemplate>
                                        <local:DashboardAdaptiveWrapPanel MinItemWidth="180" />
                                    </ItemsPanelTemplate>
                                </ItemsControl.ItemsPanel>
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Border Style="{StaticResource DashboardMetricCardStyle}">
                                            <StackPanel>
                                                <TextBlock Text="{Binding Label}" FontWeight="SemiBold" />
                                                <TextBlock Text="{Binding Value}" FontSize="22" FontWeight="SemiBold" Margin="0,4,0,2" />
                                                <TextBlock Text="{Binding Detail}" TextWrapping="Wrap" />
                                            </StackPanel>
                                        </Border>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                            <DockPanel Grid.Row="1" Margin="0,0,0,8">
                                <TextBlock Text="Search" VerticalAlignment="Center" Margin="0,0,8,0" />
                                <TextBox Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged, diag:PresentationTraceSources.TraceLevel=High}"
                                         local:DashboardFocusBehavior.FocusOnLoaded="True"
                                         VerticalContentAlignment="Center" />
                            </DockPanel>
                            <DataGrid x:Name="ItemsDataGrid"
                                      Grid.Row="2"
                                      ItemsSource="{Binding ItemsView}"
                                      SelectedItem="{Binding SelectedItem, Mode=TwoWay}"
                                      AutoGenerateColumns="False"
                                      IsReadOnly="True"
                                      EnableRowVirtualization="True"
                                      VirtualizingPanel.IsVirtualizing="True"
                                      VirtualizingPanel.VirtualizationMode="Recycling"
                                      ScrollViewer.CanContentScroll="True"
                                      ScrollViewer.IsDeferredScrollingEnabled="True"
                                      local:DashboardSelectionBehavior.ScrollSelectedItemIntoView="True"
                                      RowDetailsVisibilityMode="VisibleWhenSelected"
                                      CanUserAddRows="False">
                                <DataGrid.GroupStyle>
                                    <GroupStyle>
                                        <GroupStyle.HeaderTemplate>
                                            <DataTemplate>
                                                <Border Padding="6" Background="#EEF2F7">
                                                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold" />
                                                </Border>
                                            </DataTemplate>
                                        </GroupStyle.HeaderTemplate>
                                    </GroupStyle>
                                </DataGrid.GroupStyle>
                                <DataGrid.RowStyle>
                                    <Style TargetType="{x:Type DataGridRow}">
                                        <Setter Property="ContextMenu">
                                            <Setter.Value>
                                                <ContextMenu>
                                                    <MenuItem Header="Mark Ready"
                                                              Command="{Binding Data.MarkItemReadyCommand, Source={StaticResource DashboardCommandsProxy} }"
                                                              CommandParameter="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource AncestorType=ContextMenu} }" />
                                                    <MenuItem Header="Mark Review"
                                                              Command="{Binding Data.MarkItemForReviewCommand, Source={StaticResource DashboardCommandsProxy} }"
                                                              CommandParameter="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource AncestorType=ContextMenu} }" />
                                                </ContextMenu>
                                            </Setter.Value>
                                        </Setter>
                                    </Style>
                                </DataGrid.RowStyle>
                                <DataGrid.RowDetailsTemplate>
                                    <DataTemplate>
                                        <ContentControl Margin="28,6,8,10"
                                                        Content="{Binding}"
                                                        ContentTemplateSelector="{StaticResource DashboardItemCardTemplateSelector}" />
                                    </DataTemplate>
                                </DataGrid.RowDetailsTemplate>
                                <DataGrid.Columns>
                                    <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="2*" />
                                    <DataGridTextColumn Header="Owner" Binding="{Binding Owner}" Width="*" />
                                    <DataGridTemplateColumn Header="Status" Width="*">
                                        <DataGridTemplateColumn.CellTemplate>
                                            <DataTemplate>
                                                <Border Padding="8,2"
                                                        CornerRadius="8"
                                                        HorizontalAlignment="Left">
                                                    <Border.Background>
                                                        <Binding Path="Status" Converter="{StaticResource DashboardStatusBrushConverter}" />
                                                    </Border.Background>
                                                    <TextBlock Text="{Binding Status}" Foreground="White" FontWeight="SemiBold" />
                                                </Border>
                                            </DataTemplate>
                                        </DataGridTemplateColumn.CellTemplate>
                                    </DataGridTemplateColumn>
                                </DataGrid.Columns>
                            </DataGrid>
                        </Grid>
                    </DataTemplate>
                    <DataTemplate DataType="{x:Type local:ActivityDashboardViewModel}">
                        <ListBox ItemsSource="{Binding Activity}"
                                 VirtualizingPanel.IsVirtualizing="True"
                                 VirtualizingPanel.VirtualizationMode="Recycling"
                                 ScrollViewer.CanContentScroll="True" />
                    </DataTemplate>
                    <DataTemplate DataType="{x:Type local:SettingsDashboardViewModel}">
                        <StackPanel>
                            <TextBlock Text="{Binding Title}" FontSize="18" FontWeight="SemiBold" Margin="0,0,0,8" />
                            <TextBlock Text="{Binding Description}" TextWrapping="Wrap" Margin="0,0,0,12" />
                            <ItemsControl ItemsSource="{Binding SettingsNotes}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding}" TextWrapping="Wrap" Margin="0,0,0,6" />
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </StackPanel>
                    </DataTemplate>
                </ResourceDictionary>
            </Window.Resources>

            <Window.InputBindings>
                <KeyBinding Key="F5" Command="{Binding RefreshCommand}" />
                <KeyBinding Key="Escape" Command="{Binding CancelRefreshCommand}" />
                <KeyBinding Key="D" Modifiers="Control" Command="{x:Static local:MainWindow.FocusDetailsCommand}" />
            </Window.InputBindings>

            <Window.CommandBindings>
                <CommandBinding Command="{x:Static local:MainWindow.FocusDetailsCommand}"
                                Executed="FocusDetailsCommand_Executed"
                                CanExecute="FocusDetailsCommand_CanExecute" />
            </Window.CommandBindings>

            <DockPanel>
                <Menu DockPanel.Dock="Top">
                    <MenuItem Header="_File">
                        <MenuItem Header="_Refresh" Command="{Binding RefreshCommand}" />
                        <Separator />
                        <MenuItem Header="E_xit" />
                    </MenuItem>
                    <MenuItem Header="_Edit">
                        <MenuItem Header="_Remove selected" Command="{Binding RemoveSelectedItemCommand}" />
                    </MenuItem>
                    <MenuItem Header="_View">
                        <MenuItem Header="Overview" />
                        <MenuItem Header="Activity" />
                        <Separator />
                        <MenuItem Header="_Toggle theme" Command="{Binding ToggleThemeCommand}" />
                        <MenuItem Header="_Reset layout" Command="{Binding ResetLayoutCommand}" />
                        <Separator />
                        <MenuItem Header="_Focus details" Command="{x:Static local:MainWindow.FocusDetailsCommand}" />
                    </MenuItem>
                </Menu>

                <StatusBar DockPanel.Dock="Bottom">
                    <TextBlock Text="{Binding StatusText}" />
                    <Separator />
                    <ProgressBar Width="120"
                                 Height="14"
                                 IsIndeterminate="{Binding IsBusy}">
                        <ProgressBar.Visibility>
                            <Binding Path="IsBusy" Converter="{StaticResource BooleanToVisibilityConverter}" />
                        </ProgressBar.Visibility>
                    </ProgressBar>
                    <TextBlock Text="{Binding ProgressText}" Margin="8,0,0,0" />
                </StatusBar>

                <Grid Margin="12">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="*" />
                        <RowDefinition Height="5" />
                        <RowDefinition Height="{Binding Data.OutputPanelHeight, Source={StaticResource DashboardCommandsProxy}, Mode=TwoWay}" MinHeight="120" />
                    </Grid.RowDefinitions>

                    <Border Style="{StaticResource DashboardHeaderCardStyle}">
                        <DockPanel>
                            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                                <Button Content="{Binding ThemeButtonText}" Command="{Binding ToggleThemeCommand}" Style="{StaticResource DashboardSecondaryButtonStyle}" Margin="0,0,8,0" />
                                <Button Content="Refresh" Command="{Binding RefreshCommand}" Style="{StaticResource DashboardPrimaryButtonStyle}" />
                                <Button Content="Cancel" Margin="8,0,0,0" Command="{Binding CancelRefreshCommand}" Style="{StaticResource DashboardSecondaryButtonStyle}" />
                            </StackPanel>
                            <StackPanel>
                                <TextBlock Text="Project Dashboard" Style="{StaticResource DashboardHeaderTextStyle}" />
                                <TextBlock Text="{Binding SelectedNavigationSummary}" Style="{StaticResource DashboardSubtleTextStyle}" />
                            </StackPanel>
                        </DockPanel>
                    </Border>

                    <Grid Grid.Row="1">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="{Binding Data.NavigationColumnWidth, Source={StaticResource DashboardCommandsProxy}, Mode=TwoWay}" MinWidth="160" />
                            <ColumnDefinition Width="5" />
                            <ColumnDefinition Width="*" MinWidth="320" />
                            <ColumnDefinition Width="5" />
                            <ColumnDefinition Width="{Binding Data.DetailsColumnWidth, Source={StaticResource DashboardCommandsProxy}, Mode=TwoWay}" MinWidth="220" />
                        </Grid.ColumnDefinitions>

                        <GroupBox Header="Navigation" Style="{StaticResource DashboardPaneGroupBoxStyle}">
                            <TreeView x:Name="NavigationTreeView"
                                      ItemsSource="{Binding NavigationItems}"
                                      VirtualizingPanel.IsVirtualizing="True"
                                      VirtualizingPanel.VirtualizationMode="Recycling"
                                      ScrollViewer.CanContentScroll="True">
                                <TreeView.ItemContainerStyle>
                                    <Style TargetType="TreeViewItem">
                                        <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}" />
                                        <Setter Property="IsSelected" Value="{Binding IsSelected, Mode=TwoWay}" />
                                    </Style>
                                </TreeView.ItemContainerStyle>
                                <TreeView.ItemTemplate>
                                    <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                                        <TextBlock Text="{Binding Header}" />
                                    </HierarchicalDataTemplate>
                                </TreeView.ItemTemplate>
                            </TreeView>
                        </GroupBox>

                        <GridSplitter Grid.Column="1" Width="5" HorizontalAlignment="Stretch" />

                        <TabControl Grid.Column="2"
                                    Margin="10,0"
                                    SelectedIndex="{Binding SelectedViewIndex, Mode=TwoWay}">
                            <TabItem Header="Overview">
                                <ContentControl Content="{Binding OverviewView}" />
                            </TabItem>
                            <TabItem Header="Activity">
                                <ContentControl Content="{Binding ActivityView}" />
                            </TabItem>
                            <TabItem Header="Settings">
                                <ContentControl Content="{Binding SettingsView}" />
                            </TabItem>
                        </TabControl>

                        <GridSplitter Grid.Column="3" Width="5" HorizontalAlignment="Stretch" />

                        <GroupBox x:Name="DetailsPaneGroupBox"
                                  Grid.Column="4"
                                  Header="Details"
                                  Focusable="True"
                                  Style="{StaticResource DashboardPaneGroupBoxStyle}">
                            <ScrollViewer VerticalScrollBarVisibility="Auto">
                                <StackPanel Grid.IsSharedSizeScope="True">
                                    <Expander Header="Selected item" IsExpanded="True" Margin="0,0,0,10">
                                        <StackPanel>
                                            <TextBlock FontWeight="SemiBold" Margin="0,0,0,8">
                                                <TextBlock.Text>
                                                    <MultiBinding Converter="{StaticResource DashboardSelectionSummaryConverter}">
                                                        <Binding Path="SelectedItemName" />
                                                        <Binding Path="SelectedItemStatus" />
                                                    </MultiBinding>
                                                </TextBlock.Text>
                                            </TextBlock>
                                            <ContentControl Content="{Binding SelectedItem}"
                                                            ContentTemplate="{StaticResource DashboardDetailTemplate}"
                                                            Margin="0,0,0,16" />
                                            <Grid>
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="Auto" SharedSizeGroup="DashboardFormLabels" />
                                                    <ColumnDefinition Width="*" />
                                                </Grid.ColumnDefinitions>
                                                <Grid.RowDefinitions>
                                                    <RowDefinition Height="Auto" />
                                                    <RowDefinition Height="Auto" />
                                                    <RowDefinition Height="Auto" />
                                                </Grid.RowDefinitions>
                                                <TextBlock Grid.Row="0" Grid.Column="0" Text="Name" Style="{StaticResource DashboardFormLabelStyle}" />
                                                <TextBox Grid.Row="0" Grid.Column="1"
                                                         Text="{Binding SelectedItemName, UpdateSourceTrigger=PropertyChanged, ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True, diag:PresentationTraceSources.TraceLevel=High}"
                                                         Style="{StaticResource DashboardInputTextBoxStyle}" />
                                                <TextBlock Grid.Row="1" Grid.Column="0" Text="Owner" Style="{StaticResource DashboardFormLabelStyle}" />
                                                <TextBox Grid.Row="1" Grid.Column="1"
                                                         Text="{Binding SelectedItemOwner, UpdateSourceTrigger=PropertyChanged, ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True, diag:PresentationTraceSources.TraceLevel=High}"
                                                         Style="{StaticResource DashboardInputTextBoxStyle}" />
                                                <TextBlock Grid.Row="2" Grid.Column="0" Text="Status" Style="{StaticResource DashboardFormLabelStyle}" />
                                                <ComboBox Grid.Row="2" Grid.Column="1"
                                                          ItemsSource="{Binding StatusOptions}"
                                                          SelectedItem="{Binding SelectedItemStatus, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged, ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True, diag:PresentationTraceSources.TraceLevel=High}"
                                                          Style="{StaticResource DashboardInputComboBoxStyle}" />
                                            </Grid>
                                            <ItemsControl ItemsSource="{Binding ValidationSummary}" Margin="0,0,0,8">
                                                <ItemsControl.ItemTemplate>
                                                    <DataTemplate>
                                                        <TextBlock Text="{Binding}" Foreground="#B42318" TextWrapping="Wrap" Margin="0,0,0,4" />
                                                    </DataTemplate>
                                                </ItemsControl.ItemTemplate>
                                            </ItemsControl>
                                            <Button Content="Apply Selected" Command="{Binding ApplySelectedItemCommand}" Style="{StaticResource DashboardPrimaryButtonStyle}" />
                                            <Button Content="Remove Selected" Command="{Binding RemoveSelectedItemCommand}" Style="{StaticResource DashboardSecondaryButtonStyle}" Margin="0,8,0,0" />
                                        </StackPanel>
                                    </Expander>

                                    <Expander Header="Add item" IsExpanded="True" Margin="0,0,0,10">
                                        <StackPanel>
                                            <Grid>
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="Auto" SharedSizeGroup="DashboardFormLabels" />
                                                    <ColumnDefinition Width="*" />
                                                </Grid.ColumnDefinitions>
                                                <Grid.RowDefinitions>
                                                    <RowDefinition Height="Auto" />
                                                </Grid.RowDefinitions>
                                                <TextBlock Grid.Row="0" Grid.Column="0" Text="Name" Style="{StaticResource DashboardFormLabelStyle}" />
                                                <TextBox Grid.Row="0" Grid.Column="1"
                                                         Text="{Binding NewItemName, UpdateSourceTrigger=PropertyChanged, ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True, diag:PresentationTraceSources.TraceLevel=High}"
                                                         Style="{StaticResource DashboardInputTextBoxStyle}" />
                                            </Grid>
                                            <TextBlock Text="{Binding NewItemError}" Foreground="#B42318" TextWrapping="Wrap" Margin="0,0,0,8" />
                                            <Button Content="Add Item" Command="{Binding AddItemCommand}" Style="{StaticResource DashboardPrimaryButtonStyle}" />
                                        </StackPanel>
                                    </Expander>

                                    <Expander Header="Layout notes" IsExpanded="False">
                                        <TextBlock Text="Use splitters to resize panes. Shared-size form columns keep labels aligned across collapsible sections."
                                                   TextWrapping="Wrap" />
                                    </Expander>
                                </StackPanel>
                            </ScrollViewer>
                        </GroupBox>

                        <Border Grid.ColumnSpan="5"
                                Panel.ZIndex="10"
                                Style="{StaticResource DashboardBusyOverlayStyle}"
                                IsHitTestVisible="{Binding IsBusy}">
                            <Border.Visibility>
                                <Binding Path="IsBusy" Converter="{StaticResource BooleanToVisibilityConverter}" />
                            </Border.Visibility>
                            <Border.Triggers>
                                <EventTrigger RoutedEvent="FrameworkElement.Loaded">
                                    <BeginStoryboard>
                                        <Storyboard>
                                            <DoubleAnimation Storyboard.TargetProperty="Opacity"
                                                             From="0"
                                                             To="1"
                                                             Duration="0:0:0.18" />
                                        </Storyboard>
                                    </BeginStoryboard>
                                </EventTrigger>
                            </Border.Triggers>
                            <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
                                <ProgressBar Width="220"
                                             Height="18"
                                             IsIndeterminate="True" />
                                <TextBlock Text="{Binding ProgressText}"
                                           FontWeight="SemiBold"
                                           HorizontalAlignment="Center"
                                           Margin="0,10,0,0" />
                            </StackPanel>
                        </Border>
                    </Grid>

                    <GridSplitter Grid.Row="2"
                                  Height="5"
                                  HorizontalAlignment="Stretch"
                                  VerticalAlignment="Center"
                                  ResizeBehavior="PreviousAndNext"
                                  ResizeDirection="Rows" />

                    <GroupBox x:Name="OutputPaneGroupBox"
                              Grid.Row="3"
                              Header="Output"
                              Style="{StaticResource DashboardPaneGroupBoxStyle}"
                              Margin="0,10,0,0">
                        <TabControl SelectedIndex="{Binding SelectedOutputPanelIndex, Mode=TwoWay}">
                            <TabItem Header="Activity">
                                <ListBox ItemsSource="{Binding Activity}"
                                         VirtualizingPanel.IsVirtualizing="True"
                                         VirtualizingPanel.VirtualizationMode="Recycling"
                                         ScrollViewer.CanContentScroll="True" />
                            </TabItem>
                            <TabItem Header="Problems">
                                <DataGrid ItemsSource="{Binding Problems}"
                                          AutoGenerateColumns="False"
                                          IsReadOnly="True"
                                          CanUserAddRows="False"
                                          EnableRowVirtualization="True"
                                          VirtualizingPanel.IsVirtualizing="True"
                                          VirtualizingPanel.VirtualizationMode="Recycling"
                                          ScrollViewer.CanContentScroll="True">
                                    <DataGrid.Columns>
                                        <DataGridTextColumn Header="Severity" Binding="{Binding Severity}" Width="Auto" />
                                        <DataGridTextColumn Header="Source" Binding="{Binding Source}" Width="*" />
                                        <DataGridTextColumn Header="Message" Binding="{Binding Message}" Width="2*" />
                                    </DataGrid.Columns>
                                </DataGrid>
                            </TabItem>
                        </TabControl>
                    </GroupBox>
                </Grid>
            </DockPanel>
        </Window>
        """;

    private static string BuildWpfLocalXmlNamespace(string xamlClass)
    {
        var lastDot = xamlClass.LastIndexOf('.');
        return lastDot > 0
            ? "clr-namespace:" + xamlClass[..lastDot]
            : "clr-namespace:";
    }

    private static string BuildWpfTodoWindowXaml(string xamlClass) =>
        $"""
        <Window x:Class="{xamlClass}"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="Todo List" Height="420" Width="500">
            <Grid Margin="24">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>
                <TextBlock Text="Todo List"
                           FontSize="28"
                           FontWeight="SemiBold"
                           Margin="0,0,0,16" />
                <DockPanel Grid.Row="1" Margin="0,0,0,14">
                    <Button Content="Add"
                            Width="90"
                            DockPanel.Dock="Right"
                            Click="AddTaskButton_Click" />
                    <TextBox x:Name="TaskTextBox"
                             Height="34"
                             Margin="0,0,10,0"
                             VerticalContentAlignment="Center" />
                </DockPanel>
                <DockPanel Grid.Row="2">
                    <Button Content="Remove Selected"
                            Width="130"
                            DockPanel.Dock="Bottom"
                            Margin="0,12,0,0"
                            Click="RemoveTaskButton_Click" />
                    <ListBox x:Name="TasksListBox" />
                </DockPanel>
            </Grid>
        </Window>
        """;

    private static string BuildWpfCounterCodeBehind(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System.Windows;

            {{namespaceLine}}public partial class MainWindow : Window
            {
                private int _count;

                public MainWindow()
                {
                    InitializeComponent();
                }

                private void IncrementButton_Click(object sender, RoutedEventArgs e)
                {
                    _count++;
                    CounterTextBlock.Text = $"Count: {_count}";
                }
            }
            """;
    }

    private static string BuildWpfCalculatorCodeBehind(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System.Windows;

            {{namespaceLine}}public partial class MainWindow : Window
            {
                public MainWindow()
                {
                    InitializeComponent();
                }

                private void CalculateButton_Click(object sender, RoutedEventArgs e)
                {
                    if (!double.TryParse(FirstNumberTextBox.Text, out var firstNumber)
                        || !double.TryParse(SecondNumberTextBox.Text, out var secondNumber))
                    {
                        ResultTextBlock.Text = "Enter two valid numbers.";
                        return;
                    }

                    ResultTextBlock.Text = OperationComboBox.SelectedIndex switch
                    {
                        0 => $"{firstNumber} + {secondNumber} = {firstNumber + secondNumber}",
                        1 => $"{firstNumber} - {secondNumber} = {firstNumber - secondNumber}",
                        2 => $"{firstNumber} x {secondNumber} = {firstNumber * secondNumber}",
                        3 when secondNumber != 0 => $"{firstNumber} / {secondNumber} = {firstNumber / secondNumber}",
                        3 => "Cannot divide by zero.",
                        _ => "Choose an operation."
                    };
                }
            }
            """;
    }

    private static string BuildWpfGreetingCodeBehind(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System.Windows;

            {{namespaceLine}}public partial class MainWindow : Window
            {
                public MainWindow()
                {
                    InitializeComponent();
                }

                private void GreetButton_Click(object sender, RoutedEventArgs e)
                {
                    var name = string.IsNullOrWhiteSpace(NameTextBox.Text)
                        ? "there"
                        : NameTextBox.Text.Trim();
                    GreetingTextBlock.Text = $"Hello, {name}!";
                }
            }
            """;
    }

    private static string BuildWpfComplexDashboardCodeBehind(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System;
            using System.ComponentModel;
            using System.IO;
            using System.Text.Json;
            using System.Windows;
            using System.Windows.Input;
            using System.Windows.Media;

            {{namespaceLine}}public partial class MainWindow : Window
            {
                public static readonly RoutedUICommand FocusDetailsCommand = new(
                    "Focus Details",
                    nameof(FocusDetailsCommand),
                    typeof(MainWindow),
                    new InputGestureCollection { new KeyGesture(Key.D, ModifierKeys.Control) });

                public MainWindow()
                {
                    InitializeComponent();
                    DataContext = new MainWindowViewModel(
                        new DashboardDialogService(this),
                        new DashboardThemeService(this),
                        new DashboardLayoutStateService());
                    Loaded += MainWindow_Loaded;
                    Closing += MainWindow_Closing;
                }

                private void MainWindow_Loaded(object sender, RoutedEventArgs e)
                {
                    if (DataContext is MainWindowViewModel viewModel)
                    {
                        ApplyWindowBounds(viewModel.RestoreLayout());
                    }
                }

                private void MainWindow_Closing(object? sender, CancelEventArgs e)
                {
                    if (DataContext is MainWindowViewModel viewModel)
                    {
                        viewModel.SaveLayout(Left, Top, Width, Height);
                    }
                }

                private void ApplyWindowBounds(DashboardLayoutState state)
                {
                    if (!state.HasWindowBounds)
                    {
                        return;
                    }

                    Left = state.WindowLeft;
                    Top = state.WindowTop;
                    Width = Math.Max(MinWidth, state.WindowWidth);
                    Height = Math.Max(MinHeight, state.WindowHeight);
                }

                private void FocusDetailsCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
                {
                    e.CanExecute = DetailsPaneGroupBox is not null;
                    e.Handled = true;
                }

                private void FocusDetailsCommand_Executed(object sender, ExecutedRoutedEventArgs e)
                {
                    DetailsPaneGroupBox.Focus();
                    e.Handled = true;
                }

                private void DashboardDetailCard_PromoteRequested(object sender, RoutedEventArgs e)
                {
                    if (DataContext is not MainWindowViewModel viewModel
                        || sender is not DashboardDetailCard { Item: { } item }
                        || !viewModel.MarkItemReadyCommand.CanExecute(item))
                    {
                        return;
                    }

                    viewModel.MarkItemReadyCommand.Execute(item);
                    e.Handled = true;
                }
            }

            public sealed class DashboardDialogService : IDashboardDialogService
            {
                private readonly Window _owner;

                public DashboardDialogService(Window owner)
                {
                    _owner = owner;
                }

                public bool Confirm(DashboardDialogRequest request)
                {
                    var dialog = new DashboardConfirmDialog(request)
                    {
                        Owner = _owner
                    };

                    return dialog.ShowDialog() == true;
                }
            }

            public sealed class DashboardThemeService : IDashboardThemeService
            {
                private readonly FrameworkElement _owner;

                public DashboardThemeService(FrameworkElement owner)
                {
                    _owner = owner;
                }

                public void ApplyTheme(DashboardThemePalette palette)
                {
                    _owner.Resources["DashboardHeaderBrush"] = new SolidColorBrush(palette.Header);
                    _owner.Resources["DashboardHeaderForegroundBrush"] = new SolidColorBrush(palette.HeaderForeground);
                    _owner.Resources["DashboardSubtleForegroundBrush"] = new SolidColorBrush(palette.SubtleForeground);
                    _owner.Resources["DashboardAccentBrush"] = new SolidColorBrush(palette.Accent);
                    _owner.Resources["DashboardAccentHoverBrush"] = new SolidColorBrush(palette.AccentHover);
                }
            }

            public sealed class DashboardLayoutStateService : IDashboardLayoutStateService
            {
                private static string LayoutPath => Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GeneratedWpfDashboard",
                    "dashboard-layout.json");

                public DashboardLayoutState Load()
                {
                    try
                    {
                        if (!File.Exists(LayoutPath))
                        {
                            return DashboardLayoutState.Default;
                        }

                        var json = File.ReadAllText(LayoutPath);
                        return JsonSerializer.Deserialize<DashboardLayoutState>(json) ?? DashboardLayoutState.Default;
                    }
                    catch (IOException)
                    {
                        return DashboardLayoutState.Default;
                    }
                    catch (JsonException)
                    {
                        return DashboardLayoutState.Default;
                    }
                }

                public void Save(DashboardLayoutState state)
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(LayoutPath);
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(LayoutPath, json);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            """;
    }

    private static string BuildWpfTodoCodeBehind(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System.Collections.ObjectModel;
            using System.Windows;

            {{namespaceLine}}public partial class MainWindow : Window
            {
                private readonly ObservableCollection<string> _tasks = new();

                public MainWindow()
                {
                    InitializeComponent();
                    TasksListBox.ItemsSource = _tasks;
                }

                private void AddTaskButton_Click(object sender, RoutedEventArgs e)
                {
                    var task = TaskTextBox.Text.Trim();
                    if (task.Length == 0)
                    {
                        return;
                    }

                    _tasks.Add(task);
                    TaskTextBox.Clear();
                }

                private void RemoveTaskButton_Click(object sender, RoutedEventArgs e)
                {
                    if (TasksListBox.SelectedItem is string selectedTask)
                    {
                        _tasks.Remove(selectedTask);
                    }
                }
            }
            """;
    }

    private static bool TryBuildNewWpfViewModelPatchBlock(
        string goal,
        string fullPath,
        out string newText,
        out string note)
    {
        newText = string.Empty;
        note = string.Empty;
        if (!IsWpfComplexWindowGoal(goal)
            || !Path.GetFileName(fullPath).Equals("MainWindowViewModel.cs", StringComparison.OrdinalIgnoreCase)
            || File.Exists(fullPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        var codeBehindPath = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "MainWindow.xaml.cs");
        var namespaceName = codeBehindPath is null ? null : ExtractCSharpNamespaceName(SafeReadText(codeBehindPath));
        newText = BuildWpfComplexDashboardViewModel(namespaceName);
        note = "WPF complex-dashboard MainWindowViewModel.cs starter recipe.";
        return true;
    }

    private static bool TryBuildNewWpfAppPatchBlock(
        string goal,
        string fullPath,
        out string newText,
        out string note)
    {
        newText = string.Empty;
        note = string.Empty;
        if (!IsSimpleWpfProgramGoal(goal) || File.Exists(fullPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        if (!fileName.Equals("App.xaml", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("App.xaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        var codeBehindPath = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "MainWindow.xaml.cs");
        var namespaceName = codeBehindPath is null ? null : ExtractCSharpNamespaceName(SafeReadText(codeBehindPath));
        if (fileName.Equals("App.xaml", StringComparison.OrdinalIgnoreCase))
        {
            newText = BuildWpfAppXaml(namespaceName);
            note = "WPF App.xaml entry-point starter recipe.";
            return true;
        }

        newText = BuildWpfAppCodeBehind(namespaceName);
        note = "WPF App.xaml.cs entry-point starter recipe.";
        return true;
    }

    private static string BuildWpfAppXaml(string? namespaceName)
    {
        var appClass = string.IsNullOrWhiteSpace(namespaceName)
            ? "App"
            : namespaceName + ".App";
        return
            $$"""
            <Application x:Class="{{appClass}}"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         StartupUri="MainWindow.xaml">
                <Application.Resources>
                </Application.Resources>
            </Application>
            """;
    }

    private static string BuildWpfAppCodeBehind(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System.Windows;

            {{namespaceLine}}public partial class App : Application
            {
            }
            """;
    }

    private static bool TryBuildNewWpfStylesPatchBlock(
        string goal,
        string fullPath,
        out string newText,
        out string note)
    {
        newText = string.Empty;
        note = string.Empty;
        if (!IsWpfComplexWindowGoal(goal)
            || !Path.GetFileName(fullPath).Equals("AliDashboardStyles.xaml", StringComparison.OrdinalIgnoreCase)
            || File.Exists(fullPath))
        {
            return false;
        }

        newText = BuildWpfDashboardStylesXaml();
        note = "WPF complex-dashboard ResourceDictionary starter recipe.";
        return true;
    }

    private static bool TryBuildNewWpfUserControlPatchBlock(
        string goal,
        string fullPath,
        out string newText,
        out string note)
    {
        newText = string.Empty;
        note = string.Empty;
        if (!IsWpfComplexWindowGoal(goal) || File.Exists(fullPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        if (!fileName.Equals("DashboardDetailCard.xaml", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("DashboardDetailCard.xaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        var codeBehindPath = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "MainWindow.xaml.cs");
        var namespaceName = codeBehindPath is null ? null : ExtractCSharpNamespaceName(SafeReadText(codeBehindPath));
        if (fileName.Equals("DashboardDetailCard.xaml", StringComparison.OrdinalIgnoreCase))
        {
            newText = BuildWpfDashboardDetailCardXaml(namespaceName);
            note = "WPF complex-dashboard DashboardDetailCard.xaml UserControl starter recipe.";
            return true;
        }

        newText = BuildWpfDashboardDetailCardCodeBehind(namespaceName);
        note = "WPF complex-dashboard DashboardDetailCard.xaml.cs UserControl starter recipe.";
        return true;
    }

    private static bool TryBuildNewWpfDialogPatchBlock(
        string goal,
        string fullPath,
        out string newText,
        out string note)
    {
        newText = string.Empty;
        note = string.Empty;
        if (!IsWpfComplexWindowGoal(goal) || File.Exists(fullPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(fullPath);
        if (!fileName.Equals("DashboardConfirmDialog.xaml", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("DashboardConfirmDialog.xaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        var codeBehindPath = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "MainWindow.xaml.cs");
        var namespaceName = codeBehindPath is null ? null : ExtractCSharpNamespaceName(SafeReadText(codeBehindPath));
        if (fileName.Equals("DashboardConfirmDialog.xaml", StringComparison.OrdinalIgnoreCase))
        {
            newText = BuildWpfDashboardConfirmDialogXaml(namespaceName);
            note = "WPF complex-dashboard DashboardConfirmDialog.xaml modal-window starter recipe.";
            return true;
        }

        newText = BuildWpfDashboardConfirmDialogCodeBehind(namespaceName);
        note = "WPF complex-dashboard DashboardConfirmDialog.xaml.cs modal-window starter recipe.";
        return true;
    }

    private static string BuildWpfDashboardDetailCardXaml(string? namespaceName)
    {
        var controlClass = string.IsNullOrWhiteSpace(namespaceName)
            ? "DashboardDetailCard"
            : namespaceName + ".DashboardDetailCard";
        var localNamespace = string.IsNullOrWhiteSpace(namespaceName)
            ? "clr-namespace:"
            : "clr-namespace:" + namespaceName;
        return
            $$"""
            <UserControl x:Class="{{controlClass}}"
                         x:Name="Root"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                         xmlns:diag="clr-namespace:System.Diagnostics;assembly=WindowsBase"
                         xmlns:local="{{localNamespace}}"
                         xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                         MinHeight="130"
                         mc:Ignorable="d"
                         d:DesignHeight="180"
                         d:DesignWidth="320"
                         d:Item="{x:Static local:DashboardDesignData.DesignItem}">
                <UserControl.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary Source="AliDashboardStyles.xaml" />
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                </UserControl.Resources>

                <Grid x:Name="VisualStateRoot">
                    <VisualStateManager.VisualStateGroups>
                        <VisualStateGroup x:Name="StatusStates">
                            <VisualState x:Name="DefaultState" />
                            <VisualState x:Name="ReadyState">
                                <Storyboard>
                                    <ColorAnimation Storyboard.TargetName="StateAccentBorder"
                                                    Storyboard.TargetProperty="(Border.BorderBrush).(SolidColorBrush.Color)"
                                                    To="#17B26A"
                                                    Duration="0:0:0.18" />
                                </Storyboard>
                            </VisualState>
                            <VisualState x:Name="ReviewState">
                                <Storyboard>
                                    <ColorAnimation Storyboard.TargetName="StateAccentBorder"
                                                    Storyboard.TargetProperty="(Border.BorderBrush).(SolidColorBrush.Color)"
                                                    To="#F79009"
                                                    Duration="0:0:0.18" />
                                </Storyboard>
                            </VisualState>
                            <VisualState x:Name="DraftState">
                                <Storyboard>
                                    <ColorAnimation Storyboard.TargetName="StateAccentBorder"
                                                    Storyboard.TargetProperty="(Border.BorderBrush).(SolidColorBrush.Color)"
                                                    To="#6941C6"
                                                    Duration="0:0:0.18" />
                                </Storyboard>
                            </VisualState>
                        </VisualStateGroup>
                    </VisualStateManager.VisualStateGroups>

                    <Border x:Name="StateAccentBorder"
                            Style="{StaticResource DashboardDetailCardStyle}"
                            BorderBrush="#D0D7DE">
                        <StackPanel>
                            <TextBlock Text="Selected item" FontWeight="SemiBold" Margin="0,0,0,8" />
                            <TextBlock Text="{Binding Item.Name, ElementName=Root, TargetNullValue=No item selected, diag:PresentationTraceSources.TraceLevel=High}" FontSize="18" FontWeight="SemiBold" TextWrapping="Wrap" />
                            <TextBlock Margin="0,8,0,0">
                                <Run Text="Owner: " />
                                <Run Text="{Binding Item.Owner, ElementName=Root, TargetNullValue=none}" />
                            </TextBlock>
                            <TextBlock Margin="0,4,0,0">
                                <Run Text="Status: " />
                                <Run Text="{Binding Item.Status, ElementName=Root, TargetNullValue=none}" />
                            </TextBlock>
                            <Button Content="Mark Ready"
                                    Style="{StaticResource DashboardSecondaryButtonStyle}"
                                    Click="PromoteButton_Click"
                                    HorizontalAlignment="Left"
                                    Margin="0,10,0,0" />
                        </StackPanel>
                    </Border>
                </Grid>
            </UserControl>
            """;
    }

    private static string BuildWpfDashboardDetailCardCodeBehind(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System;
            using System.Windows;
            using System.Windows.Controls;

            {{namespaceLine}}public partial class DashboardDetailCard : UserControl
            {
                public static readonly DependencyProperty ItemProperty =
                    DependencyProperty.Register(
                        nameof(Item),
                        typeof(MainWindowViewModel.DashboardItem),
                        typeof(DashboardDetailCard),
                        new PropertyMetadata(null, OnItemChanged));

                public static readonly RoutedEvent PromoteRequestedEvent =
                    EventManager.RegisterRoutedEvent(
                        nameof(PromoteRequested),
                        RoutingStrategy.Bubble,
                        typeof(RoutedEventHandler),
                        typeof(DashboardDetailCard));

                public DashboardDetailCard()
                {
                    InitializeComponent();
                    Loaded += DashboardDetailCard_Loaded;
                }

                public event RoutedEventHandler PromoteRequested
                {
                    add => AddHandler(PromoteRequestedEvent, value);
                    remove => RemoveHandler(PromoteRequestedEvent, value);
                }

                public MainWindowViewModel.DashboardItem? Item
                {
                    get => (MainWindowViewModel.DashboardItem?)GetValue(ItemProperty);
                    set => SetValue(ItemProperty, value);
                }

                private void PromoteButton_Click(object sender, RoutedEventArgs e)
                {
                    RaiseEvent(new RoutedEventArgs(PromoteRequestedEvent, this));
                }

                private static void OnItemChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
                {
                    if (dependencyObject is DashboardDetailCard card)
                    {
                        card.UpdateVisualState(true);
                    }
                }

                private void DashboardDetailCard_Loaded(object sender, RoutedEventArgs e) =>
                    UpdateVisualState(false);

                private void UpdateVisualState(bool useTransitions)
                {
                    var status = Item?.Status ?? string.Empty;
                    var state = status.Equals("Ready", StringComparison.OrdinalIgnoreCase)
                        ? "ReadyState"
                        : status.Equals("Review", StringComparison.OrdinalIgnoreCase)
                            ? "ReviewState"
                            : status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
                                ? "DraftState"
                                : "DefaultState";
                    VisualStateManager.GoToElementState(VisualStateRoot, state, useTransitions);
                }
            }
            """;
    }

    private static string BuildWpfDashboardConfirmDialogXaml(string? namespaceName)
    {
        var dialogClass = string.IsNullOrWhiteSpace(namespaceName)
            ? "DashboardConfirmDialog"
            : namespaceName + ".DashboardConfirmDialog";
        return
            $$"""
            <Window x:Class="{{dialogClass}}"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Title="{Binding Title}"
                    Width="420"
                    MinWidth="360"
                    SizeToContent="Height"
                    ResizeMode="NoResize"
                    WindowStartupLocation="CenterOwner"
                    ShowInTaskbar="False">
                <Window.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary Source="AliDashboardStyles.xaml" />
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                </Window.Resources>

                <Grid Margin="20">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="Auto" />
                    </Grid.RowDefinitions>

                    <TextBlock Text="{Binding Title}"
                               FontSize="18"
                               FontWeight="SemiBold"
                               TextWrapping="Wrap" />
                    <TextBlock Grid.Row="1"
                               Text="{Binding Message}"
                               TextWrapping="Wrap"
                               Margin="0,10,0,20" />
                    <StackPanel Grid.Row="2"
                                Orientation="Horizontal"
                                HorizontalAlignment="Right">
                        <Button Content="{Binding CancelText}"
                                IsCancel="True"
                                MinWidth="96"
                                Style="{StaticResource DashboardSecondaryButtonStyle}"
                                Margin="0,0,8,0" />
                        <Button Content="{Binding PrimaryText}"
                                IsDefault="True"
                                MinWidth="96"
                                Style="{StaticResource DashboardPrimaryButtonStyle}"
                                Click="PrimaryButton_Click" />
                    </StackPanel>
                </Grid>
            </Window>
            """;
    }

    private static string BuildWpfDashboardConfirmDialogCodeBehind(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System.Windows;

            {{namespaceLine}}public partial class DashboardConfirmDialog : Window
            {
                public DashboardConfirmDialog(DashboardDialogRequest request)
                {
                    InitializeComponent();
                    DataContext = new DashboardConfirmDialogViewModel(request);
                }

                private void PrimaryButton_Click(object sender, RoutedEventArgs e)
                {
                    DialogResult = true;
                    Close();
                }
            }

            public sealed class DashboardConfirmDialogViewModel
            {
                public DashboardConfirmDialogViewModel(DashboardDialogRequest request)
                {
                    Title = request.Title;
                    Message = request.Message;
                    PrimaryText = request.PrimaryText;
                    CancelText = request.CancelText;
                }

                public string Title { get; }

                public string Message { get; }

                public string PrimaryText { get; }

                public string CancelText { get; }
            }
            """;
    }

    private static string BuildWpfDashboardStylesXaml() =>
        """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <SolidColorBrush x:Key="DashboardHeaderBrush" Color="#202A36" />
            <SolidColorBrush x:Key="DashboardHeaderForegroundBrush" Color="#FFFFFF" />
            <SolidColorBrush x:Key="DashboardSubtleForegroundBrush" Color="#C8D2DF" />
            <SolidColorBrush x:Key="DashboardAccentBrush" Color="#1F6FEB" />
            <SolidColorBrush x:Key="DashboardAccentHoverBrush" Color="#2F81F7" />

            <Style x:Key="DashboardHeaderCardStyle" TargetType="Border">
                <Setter Property="Padding" Value="14" />
                <Setter Property="Margin" Value="0,0,0,12" />
                <Setter Property="Background" Value="{DynamicResource DashboardHeaderBrush}" />
                <Setter Property="CornerRadius" Value="4" />
            </Style>

            <Style x:Key="DashboardHeaderTextStyle" TargetType="TextBlock">
                <Setter Property="FontSize" Value="24" />
                <Setter Property="FontWeight" Value="SemiBold" />
                <Setter Property="Foreground" Value="{DynamicResource DashboardHeaderForegroundBrush}" />
            </Style>

            <Style x:Key="DashboardSubtleTextStyle" TargetType="TextBlock">
                <Setter Property="Foreground" Value="{DynamicResource DashboardSubtleForegroundBrush}" />
                <Setter Property="TextWrapping" Value="Wrap" />
            </Style>

            <Style x:Key="DashboardFormLabelStyle" TargetType="TextBlock">
                <Setter Property="FontWeight" Value="SemiBold" />
                <Setter Property="Margin" Value="0,0,0,4" />
            </Style>

            <Style x:Key="DashboardButtonBaseStyle" TargetType="Button">
                <Setter Property="MinWidth" Value="92" />
                <Setter Property="Height" Value="34" />
                <Setter Property="Padding" Value="12,0" />
                <Setter Property="FontWeight" Value="SemiBold" />
                <Style.Triggers>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Opacity" Value="0.55" />
                    </Trigger>
                </Style.Triggers>
            </Style>

            <Style x:Key="DashboardPrimaryButtonStyle" TargetType="Button" BasedOn="{StaticResource DashboardButtonBaseStyle}">
                <Setter Property="Background" Value="{DynamicResource DashboardAccentBrush}" />
                <Setter Property="Foreground" Value="White" />
                <Setter Property="BorderThickness" Value="0" />
                <Style.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter Property="Background" Value="{DynamicResource DashboardAccentHoverBrush}" />
                    </Trigger>
                </Style.Triggers>
            </Style>

            <Style x:Key="DashboardSecondaryButtonStyle" TargetType="Button" BasedOn="{StaticResource DashboardButtonBaseStyle}">
                <Setter Property="Background" Value="#FFFFFF" />
                <Setter Property="Foreground" Value="#1F2328" />
                <Setter Property="BorderBrush" Value="#D0D7DE" />
                <Setter Property="BorderThickness" Value="1" />
                <Style.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter Property="Background" Value="#F6F8FA" />
                    </Trigger>
                </Style.Triggers>
            </Style>

            <Style x:Key="DashboardInputTextBoxStyle" TargetType="TextBox">
                <Setter Property="Height" Value="32" />
                <Setter Property="Margin" Value="0,0,0,8" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
                <Setter Property="Padding" Value="6,0" />
                <Setter Property="Validation.ErrorTemplate">
                    <Setter.Value>
                        <ControlTemplate>
                            <DockPanel LastChildFill="True">
                                <Border Width="20"
                                        Height="20"
                                        Margin="6,0,0,0"
                                        Background="#B42318"
                                        CornerRadius="10"
                                        DockPanel.Dock="Right">
                                    <TextBlock Text="!"
                                               Foreground="White"
                                               FontWeight="SemiBold"
                                               HorizontalAlignment="Center"
                                               VerticalAlignment="Center" />
                                </Border>
                                <AdornedElementPlaceholder />
                            </DockPanel>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
                <Setter Property="ToolTip" Value="{Binding RelativeSource={RelativeSource Self}, Path=(Validation.Errors)[0].ErrorContent}" />
                <Style.Triggers>
                    <Trigger Property="Validation.HasError" Value="True">
                        <Setter Property="BorderBrush" Value="#B42318" />
                        <Setter Property="BorderThickness" Value="2" />
                    </Trigger>
                </Style.Triggers>
            </Style>

            <Style x:Key="DashboardInputComboBoxStyle" TargetType="ComboBox">
                <Setter Property="Height" Value="32" />
                <Setter Property="Margin" Value="0,0,0,8" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
                <Setter Property="Padding" Value="6,0" />
            </Style>

            <Style x:Key="DashboardMetricCardStyle" TargetType="Border">
                <Setter Property="Padding" Value="10" />
                <Setter Property="Margin" Value="0,0,8,0" />
                <Setter Property="BorderThickness" Value="1" />
                <Setter Property="BorderBrush" Value="#D0D7DE" />
                <Setter Property="Background" Value="#F6F8FA" />
                <Setter Property="CornerRadius" Value="4" />
            </Style>

            <Style x:Key="DashboardPaneGroupBoxStyle" TargetType="GroupBox">
                <Setter Property="Padding" Value="10" />
                <Setter Property="Margin" Value="0" />
            </Style>

            <Style x:Key="DashboardBusyOverlayStyle" TargetType="Border">
                <Setter Property="Background" Value="#CCFFFFFF" />
                <Setter Property="Opacity" Value="0" />
            </Style>

            <Style x:Key="DashboardDetailCardStyle" TargetType="Border">
                <Setter Property="Padding" Value="12" />
                <Setter Property="Margin" Value="0" />
                <Setter Property="BorderThickness" Value="1" />
                <Setter Property="BorderBrush" Value="#D0D7DE" />
                <Setter Property="CornerRadius" Value="4" />
            </Style>
        </ResourceDictionary>
        """;

    private static string BuildWpfComplexDashboardViewModel(string? namespaceName)
    {
        var namespaceLine = string.IsNullOrWhiteSpace(namespaceName)
            ? string.Empty
            : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}";
        return
            $$"""
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Collections.ObjectModel;
            using System.ComponentModel;
            using System.Globalization;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Threading.Tasks;
            using System.Windows;
            using System.Windows.Controls;
            using System.Windows.Controls.Primitives;
            using System.Windows.Data;
            using System.Windows.Input;
            using System.Windows.Media;
            using System.Windows.Threading;

            {{namespaceLine}}public sealed class MainWindowViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
            {
                private readonly Dictionary<string, List<string>> _errors = new();
                private readonly IDashboardDialogService _dialogService;
                private readonly IDashboardThemeService _themeService;
                private readonly IDashboardLayoutStateService _layoutStateService;
                private readonly DispatcherTimer _searchRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
                private string _newItemName = string.Empty;
                private string _newItemError = string.Empty;
                private string _searchText = string.Empty;
                private CancellationTokenSource? _refreshCancellation;
                private bool _isBusy;
                private string _progressText = "Idle";
                private DashboardNavigationItem? _selectedNavigation;
                private string _selectedNavigationSummary = "Navigation, tabs, data, details, and status in one resizable WPF shell.";
                private object? _selectedWorkspaceView;
                private int _selectedViewIndex;
                private bool _synchronizingNavigation;
                private DashboardItem? _selectedItem;
                private string _selectedItemError = string.Empty;
                private string _selectedItemName = string.Empty;
                private string _selectedItemOwner = string.Empty;
                private string _selectedItemStatus = string.Empty;
                private string _statusText = "Ready";
                private bool _isDarkTheme = true;
                private string _themeButtonText = "Light Theme";
                private GridLength _navigationColumnWidth = new(220d);
                private GridLength _detailsColumnWidth = new(280d);
                private GridLength _outputPanelHeight = new(160d);
                private int _selectedOutputPanelIndex;

                public MainWindowViewModel(
                    IDashboardDialogService? dialogService = null,
                    IDashboardThemeService? themeService = null,
                    IDashboardLayoutStateService? layoutStateService = null)
                {
                    _dialogService = dialogService ?? new NullDashboardDialogService();
                    _themeService = themeService ?? new NullDashboardThemeService();
                    _layoutStateService = layoutStateService ?? new NullDashboardLayoutStateService();
                    _searchRefreshTimer.Tick += SearchRefreshTimer_Tick;
                    ItemsView = CollectionViewSource.GetDefaultView(Items);
                    ItemsView.Filter = FilterItem;
                    ConfigureItemsView();
                    RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
                    CancelRefreshCommand = new RelayCommand(_ => CancelRefresh(), _ => IsBusy);
                    ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
                    ResetLayoutCommand = new RelayCommand(_ => ResetLayout(), _ => !IsBusy);
                    AddItemCommand = new RelayCommand(_ => AddItem(), _ => CanAddItem());
                    ApplySelectedItemCommand = new RelayCommand(_ => ApplySelectedItem(), _ => CanApplySelectedItem());
                    MarkItemReadyCommand = new RelayCommand(parameter => MarkItemStatus(parameter, "Ready"), parameter => CanMarkItemStatus(parameter, "Ready"));
                    MarkItemForReviewCommand = new RelayCommand(parameter => MarkItemStatus(parameter, "Review"), parameter => CanMarkItemStatus(parameter, "Review"));
                    RemoveSelectedItemCommand = new RelayCommand(_ => RemoveSelectedItem(), _ => CanRemoveSelectedItem());
                    OverviewView = new OverviewDashboardViewModel(this);
                    ActivityView = new ActivityDashboardViewModel(this);
                    SettingsView = new SettingsDashboardViewModel();
                    SeedNavigation();
                    SeedDashboard();
                    _themeService.ApplyTheme(DashboardThemePalette.Dark);
                }

                public event PropertyChangedEventHandler? PropertyChanged;

                public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

                public ObservableCollection<DashboardItem> Items { get; } = new();

                public ObservableCollection<DashboardMetric> Metrics { get; } = new();

                public ObservableCollection<string> StatusOptions { get; } = new() { "New", "Draft", "Ready", "Review" };

                public ObservableCollection<string> ValidationSummary { get; } = new();

                public ICollectionView ItemsView { get; }

                public ObservableCollection<string> Activity { get; } = new();

                public ObservableCollection<DashboardProblem> Problems { get; } = new();

                public ObservableCollection<DashboardNavigationItem> NavigationItems { get; } = new();

                public OverviewDashboardViewModel OverviewView { get; }

                public ActivityDashboardViewModel ActivityView { get; }

                public SettingsDashboardViewModel SettingsView { get; }

                public ICommand RefreshCommand { get; }

                public ICommand CancelRefreshCommand { get; }

                public ICommand ToggleThemeCommand { get; }

                public ICommand ResetLayoutCommand { get; }

                public ICommand AddItemCommand { get; }

                public ICommand ApplySelectedItemCommand { get; }

                public ICommand MarkItemReadyCommand { get; }

                public ICommand MarkItemForReviewCommand { get; }

                public ICommand RemoveSelectedItemCommand { get; }

                public string NewItemName
                {
                    get => _newItemName;
                    set
                    {
                        if (SetField(ref _newItemName, value))
                        {
                            ValidateNewItemName();
                        }
                    }
                }

                public string NewItemError
                {
                    get => _newItemError;
                    private set => SetField(ref _newItemError, value);
                }

                public bool HasErrors => _errors.Count > 0;

                public DashboardNavigationItem? SelectedNavigation
                {
                    get => _selectedNavigation;
                    private set => SetField(ref _selectedNavigation, value);
                }

                public string SelectedNavigationSummary
                {
                    get => _selectedNavigationSummary;
                    private set => SetField(ref _selectedNavigationSummary, value);
                }

                public object? SelectedWorkspaceView
                {
                    get => _selectedWorkspaceView;
                    private set => SetField(ref _selectedWorkspaceView, value);
                }

                public int SelectedViewIndex
                {
                    get => _selectedViewIndex;
                    set
                    {
                        if (SetField(ref _selectedViewIndex, value) && !_synchronizingNavigation)
                        {
                            SelectNavigationByViewIndex(value);
                        }
                    }
                }

                public string SearchText
                {
                    get => _searchText;
                    set
                    {
                        var normalized = value ?? string.Empty;
                        if (SetField(ref _searchText, normalized))
                        {
                            ScheduleSearchRefresh();
                            StatusText = string.IsNullOrWhiteSpace(SearchText)
                                ? "Search cleared - refreshing"
                                : $"Search queued for \"{SearchText}\"";
                        }
                    }
                }

                public bool IsBusy
                {
                    get => _isBusy;
                    private set
                    {
                        if (SetField(ref _isBusy, value))
                        {
                            ((AsyncRelayCommand)RefreshCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)CancelRefreshCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)AddItemCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)ApplySelectedItemCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)MarkItemReadyCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)MarkItemForReviewCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)RemoveSelectedItemCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)ResetLayoutCommand).RaiseCanExecuteChanged();
                        }
                    }
                }

                public string ProgressText
                {
                    get => _progressText;
                    private set => SetField(ref _progressText, value);
                }

                public DashboardItem? SelectedItem
                {
                    get => _selectedItem;
                    set
                    {
                        if (SetField(ref _selectedItem, value))
                        {
                            LoadSelectedItemEditor();
                            ((RelayCommand)ApplySelectedItemCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)RemoveSelectedItemCommand).RaiseCanExecuteChanged();
                        }
                    }
                }

                public string SelectedItemName
                {
                    get => _selectedItemName;
                    set
                    {
                        if (SetField(ref _selectedItemName, value ?? string.Empty))
                        {
                            ValidateSelectedItemEditor();
                        }
                    }
                }

                public string SelectedItemOwner
                {
                    get => _selectedItemOwner;
                    set
                    {
                        if (SetField(ref _selectedItemOwner, value ?? string.Empty))
                        {
                            ValidateSelectedItemEditor();
                        }
                    }
                }

                public string SelectedItemStatus
                {
                    get => _selectedItemStatus;
                    set
                    {
                        if (SetField(ref _selectedItemStatus, value ?? string.Empty))
                        {
                            ValidateSelectedItemEditor();
                        }
                    }
                }

                public string SelectedItemError
                {
                    get => _selectedItemError;
                    private set => SetField(ref _selectedItemError, value);
                }

                public string StatusText
                {
                    get => _statusText;
                    private set => SetField(ref _statusText, value);
                }

                public bool IsDarkTheme
                {
                    get => _isDarkTheme;
                    private set => SetField(ref _isDarkTheme, value);
                }

                public string ThemeButtonText
                {
                    get => _themeButtonText;
                    private set => SetField(ref _themeButtonText, value);
                }

                public GridLength NavigationColumnWidth
                {
                    get => _navigationColumnWidth;
                    set => SetField(ref _navigationColumnWidth, value);
                }

                public GridLength DetailsColumnWidth
                {
                    get => _detailsColumnWidth;
                    set => SetField(ref _detailsColumnWidth, value);
                }

                public GridLength OutputPanelHeight
                {
                    get => _outputPanelHeight;
                    set => SetField(ref _outputPanelHeight, value);
                }

                public int SelectedOutputPanelIndex
                {
                    get => _selectedOutputPanelIndex;
                    set => SetField(ref _selectedOutputPanelIndex, value);
                }

                private void SeedNavigation()
                {
                    NavigationItems.Clear();
                    var overview = CreateNavigationItem("Overview", "Review current dashboard items, filters, and selected detail records.", OverviewView, 0);
                    var activity = CreateNavigationItem("Activity", "Inspect recent dashboard activity and refresh history.", ActivityView, 1);
                    var settings = CreateNavigationItem("Settings", "Review local configuration notes for this dashboard shell.", SettingsView, 2);
                    var workspace = CreateNavigationItem("Workspace", "Project workspace navigation.", OverviewView, 0);
                    workspace.IsExpanded = true;
                    workspace.Children.Add(overview);
                    workspace.Children.Add(activity);
                    workspace.Children.Add(settings);
                    NavigationItems.Add(workspace);
                    overview.IsSelected = true;
                }

                private DashboardNavigationItem CreateNavigationItem(string header, string summary, object view, int viewIndex) =>
                    new(header, summary, view, viewIndex, SelectNavigationItem);

                private void SelectNavigationByViewIndex(int viewIndex)
                {
                    var match = FindNavigationItemByViewIndex(NavigationItems, viewIndex);
                    if (match is not null)
                    {
                        match.IsSelected = true;
                    }
                }

                private DashboardNavigationItem? FindNavigationItemByViewIndex(IEnumerable<DashboardNavigationItem> items, int viewIndex)
                {
                    foreach (var item in items)
                    {
                        if (item.ViewIndex == viewIndex && item.Children.Count == 0)
                        {
                            return item;
                        }

                        var childMatch = FindNavigationItemByViewIndex(item.Children, viewIndex);
                        if (childMatch is not null)
                        {
                            return childMatch;
                        }
                    }

                    return null;
                }

                private void SelectNavigationItem(DashboardNavigationItem item)
                {
                    if (_synchronizingNavigation)
                    {
                        return;
                    }

                    _synchronizingNavigation = true;
                    try
                    {
                        if (SelectedNavigation is not null && !ReferenceEquals(SelectedNavigation, item))
                        {
                            SelectedNavigation.IsSelected = false;
                        }

                        SelectedNavigation = item;
                        SelectedNavigationSummary = item.Summary;
                        SelectedWorkspaceView = item.View;
                        SelectedViewIndex = item.ViewIndex;
                        StatusText = $"View: {item.Header}";
                    }
                    finally
                    {
                        _synchronizingNavigation = false;
                    }
                }

                private void SeedDashboard()
                {
                    using (ItemsView.DeferRefresh())
                    {
                        Items.Clear();
                        Items.Add(new DashboardItem("Intake workflow", "Ali", "Ready"));
                        Items.Add(new DashboardItem("Validation queue", "Owner", "Review"));
                        Items.Add(new DashboardItem("Release packet", "Ali", "Draft"));
                    }

                    Activity.Clear();
                    Activity.Add("Dashboard loaded.");
                    Activity.Add("Three sample work items are ready for review.");
                    Problems.Clear();
                    Problems.Add(new DashboardProblem("Info", "Dashboard", "No blocking problems detected."));
                    Problems.Add(new DashboardProblem("Review", "Validation queue", "One item still needs review before release."));
                    SelectedItem = FirstVisibleItem();
                    UpdateMetrics();
                    StatusText = "Ready - 3 items loaded";
                }

                private async Task RefreshAsync()
                {
                    using var cancellation = new CancellationTokenSource();
                    _refreshCancellation = cancellation;
                    try
                    {
                        IsBusy = true;
                        ProgressText = "Refreshing data...";
                        StatusText = "Refreshing dashboard";
                        await Task.Delay(250, cancellation.Token);
                        ItemsView.Refresh();
                        UpdateMetrics();
                        Activity.Insert(0, $"Refreshed at {DateTime.Now:t}.");
                        ProgressText = "Refresh complete";
                        StatusText = "Dashboard refreshed";
                    }
                    catch (OperationCanceledException)
                    {
                        Activity.Insert(0, "Refresh canceled.");
                        ProgressText = "Refresh canceled";
                        StatusText = "Refresh canceled";
                    }
                    finally
                    {
                        _refreshCancellation = null;
                        IsBusy = false;
                    }
                }

                private void CancelRefresh() => _refreshCancellation?.Cancel();

                private void ScheduleSearchRefresh()
                {
                    _searchRefreshTimer.Stop();
                    _searchRefreshTimer.Start();
                }

                private void SearchRefreshTimer_Tick(object? sender, EventArgs args)
                {
                    _searchRefreshTimer.Stop();
                    ApplySearchRefresh();
                }

                private void ApplySearchRefresh()
                {
                    ItemsView.Refresh();
                    SelectedItem = FirstVisibleItem();
                    UpdateMetrics();
                    StatusText = string.IsNullOrWhiteSpace(SearchText)
                        ? $"Ready - {Items.Count} items loaded"
                        : $"Filtered by \"{SearchText}\"";
                }

                private void ToggleTheme()
                {
                    IsDarkTheme = !IsDarkTheme;
                    _themeService.ApplyTheme(IsDarkTheme ? DashboardThemePalette.Dark : DashboardThemePalette.Light);
                    ThemeButtonText = IsDarkTheme ? "Light Theme" : "Dark Theme";
                    var themeName = IsDarkTheme ? "Dark" : "Light";
                    Activity.Insert(0, $"{themeName} theme applied.");
                    StatusText = $"{themeName} theme applied";
                }

                private void ResetLayout()
                {
                    NavigationColumnWidth = new GridLength(220d);
                    DetailsColumnWidth = new GridLength(280d);
                    Activity.Insert(0, "Layout reset.");
                    StatusText = "Layout reset";
                }

                public DashboardLayoutState RestoreLayout()
                {
                    var state = _layoutStateService.Load();
                    NavigationColumnWidth = new GridLength(Math.Max(160d, state.NavigationColumnWidth));
                    DetailsColumnWidth = new GridLength(Math.Max(220d, state.DetailsColumnWidth));
                    OutputPanelHeight = new GridLength(Math.Max(120d, state.OutputPanelHeight));
                    SelectedOutputPanelIndex = Math.Clamp(state.SelectedOutputPanelIndex, 0, 1);
                    Activity.Insert(0, "Layout restored.");
                    StatusText = "Layout restored";
                    return state;
                }

                public void SaveLayout(double windowLeft, double windowTop, double windowWidth, double windowHeight)
                {
                    var state = new DashboardLayoutState(
                        Math.Max(160d, NavigationColumnWidth.Value),
                        Math.Max(220d, DetailsColumnWidth.Value),
                        Math.Max(120d, OutputPanelHeight.Value),
                        Math.Clamp(SelectedOutputPanelIndex, 0, 1),
                        windowLeft,
                        windowTop,
                        Math.Max(760d, windowWidth),
                        Math.Max(520d, windowHeight));
                    _layoutStateService.Save(state);
                }

                public IEnumerable GetErrors(string? propertyName)
                {
                    if (string.IsNullOrWhiteSpace(propertyName))
                    {
                        var allErrors = new List<string>();
                        foreach (var errors in _errors.Values)
                        {
                            allErrors.AddRange(errors);
                        }

                        return allErrors;
                    }

                    return _errors.TryGetValue(propertyName, out var propertyErrors)
                        ? propertyErrors
                        : Array.Empty<string>();
                }

                private bool CanAddItem() => !IsBusy && !HasErrors && !string.IsNullOrWhiteSpace(NewItemName);

                private bool CanApplySelectedItem() =>
                    !IsBusy
                    && SelectedItem is not null
                    && SelectedItemName.Trim().Length > 0
                    && SelectedItemOwner.Trim().Length > 0
                    && StatusOptions.Contains(SelectedItemStatus)
                    && ValidationSummary.Count == 0;

                private bool CanMarkItemStatus(object? parameter, string status) =>
                    !IsBusy
                    && parameter is DashboardItem item
                    && !item.Status.Equals(status, StringComparison.OrdinalIgnoreCase);

                private bool CanRemoveSelectedItem() => !IsBusy && SelectedItem is not null;

                private void AddItem()
                {
                    var name = NewItemName.Trim();
                    if (name.Length == 0)
                    {
                        StatusText = "Enter an item name first.";
                        return;
                    }

                    ValidateNewItemName();
                    if (!CanAddItem())
                    {
                        StatusText = string.IsNullOrWhiteSpace(NewItemError) ? "Fix the item name first." : NewItemError;
                        return;
                    }

                    var item = new DashboardItem(name, "Owner", "New");
                    using (ItemsView.DeferRefresh())
                    {
                        Items.Add(item);
                    }

                    UpdateMetrics();
                    SelectedItem = FilterItem(item) ? item : FirstVisibleItem();
                    RaiseRowActionCanExecuteChanged();
                    Activity.Insert(0, $"Added {name}.");
                    NewItemName = string.Empty;
                    StatusText = $"Added {name}";
                }

                private void ApplySelectedItem()
                {
                    if (SelectedItem is not { } item)
                    {
                        StatusText = "Select an item first.";
                        return;
                    }

                    ValidateSelectedItemEditor();
                    if (!CanApplySelectedItem())
                    {
                        StatusText = string.IsNullOrWhiteSpace(SelectedItemError) ? "Fix the selected item first." : SelectedItemError;
                        return;
                    }

                    var updated = item with
                    {
                        Name = SelectedItemName.Trim(),
                        Owner = SelectedItemOwner.Trim(),
                        Status = SelectedItemStatus
                    };
                    var index = Items.IndexOf(item);
                    if (index >= 0)
                    {
                        using (ItemsView.DeferRefresh())
                        {
                            Items[index] = updated;
                        }
                    }

                    UpdateMetrics();
                    SelectedItem = updated;
                    RaiseRowActionCanExecuteChanged();
                    Activity.Insert(0, $"Updated {updated.Name}.");
                    StatusText = $"Updated {updated.Name}";
                }

                private void MarkItemStatus(object? parameter, string status)
                {
                    if (parameter is not DashboardItem item)
                    {
                        StatusText = "Choose a row first.";
                        return;
                    }

                    var index = Items.IndexOf(item);
                    if (index < 0)
                    {
                        StatusText = "That row is no longer available.";
                        return;
                    }

                    var updated = item with { Status = status };
                    using (ItemsView.DeferRefresh())
                    {
                        Items[index] = updated;
                    }

                    UpdateMetrics();
                    SelectedItem = updated;
                    Activity.Insert(0, $"Marked {updated.Name} as {status}.");
                    StatusText = $"Marked {updated.Name} as {status}";
                    RaiseRowActionCanExecuteChanged();
                }

                private void RemoveSelectedItem()
                {
                    if (SelectedItem is not { } item)
                    {
                        StatusText = "Select an item first.";
                        return;
                    }

                    var request = new DashboardDialogRequest("Remove selected item", $"Remove {item.Name}?");
                    if (!_dialogService.Confirm(request))
                    {
                        StatusText = "Remove canceled";
                        return;
                    }

                    using (ItemsView.DeferRefresh())
                    {
                        Items.Remove(item);
                    }

                    UpdateMetrics();
                    SelectedItem = FirstVisibleItem();
                    RaiseRowActionCanExecuteChanged();
                    Activity.Insert(0, $"Removed {item.Name}.");
                    StatusText = $"Removed {item.Name}";
                    ValidateNewItemName();
                }

                private void LoadSelectedItemEditor()
                {
                    if (SelectedItem is null)
                    {
                        SelectedItemName = string.Empty;
                        SelectedItemOwner = string.Empty;
                        SelectedItemStatus = StatusOptions[0];
                    }
                    else
                    {
                        SelectedItemName = SelectedItem.Name;
                        SelectedItemOwner = SelectedItem.Owner;
                        SelectedItemStatus = StatusOptions.Contains(SelectedItem.Status) ? SelectedItem.Status : StatusOptions[0];
                    }

                    ValidateSelectedItemEditor();
                }

                private void UpdateMetrics()
                {
                    Metrics.Clear();
                    Metrics.Add(new DashboardMetric("Total", Items.Count.ToString(CultureInfo.InvariantCulture), "All dashboard items"));
                    Metrics.Add(new DashboardMetric("Visible", CountVisibleItems().ToString(CultureInfo.InvariantCulture), "Rows after search/filter"));
                    Metrics.Add(new DashboardMetric("Review", CountItemsByStatus("Review").ToString(CultureInfo.InvariantCulture), "Items needing attention"));
                }

                private int CountVisibleItems()
                {
                    var count = 0;
                    foreach (var _ in ItemsView)
                    {
                        count++;
                    }

                    return count;
                }

                private int CountItemsByStatus(string status)
                {
                    var count = 0;
                    foreach (var item in Items)
                    {
                        if (item.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
                        {
                            count++;
                        }
                    }

                    return count;
                }

                private void ValidateNewItemName()
                {
                    var errors = new List<string>();
                    var name = NewItemName.Trim();
                    if (name.Length > 60)
                    {
                        errors.Add("Use 60 characters or fewer.");
                    }

                    if (name.Length > 0 && ContainsItemName(name))
                    {
                        errors.Add("An item with this name already exists.");
                    }

                    SetErrors(nameof(NewItemName), errors);
                    NewItemError = errors.Count > 0 ? errors[0] : string.Empty;
                    ((RelayCommand)AddItemCommand).RaiseCanExecuteChanged();
                }

                private void ValidateSelectedItemEditor()
                {
                    ValidationSummary.Clear();
                    if (SelectedItem is null)
                    {
                        SelectedItemError = "Select an item to edit.";
                    }
                    else if (SelectedItemName.Trim().Length == 0)
                    {
                        SelectedItemError = "Enter a selected item name.";
                    }
                    else if (SelectedItemOwner.Trim().Length == 0)
                    {
                        SelectedItemError = "Enter a selected item owner.";
                    }
                    else if (!StatusOptions.Contains(SelectedItemStatus))
                    {
                        SelectedItemError = "Choose a valid status.";
                    }
                    else
                    {
                        SelectedItemError = string.Empty;
                    }

                    if (!string.IsNullOrWhiteSpace(SelectedItemError))
                    {
                        ValidationSummary.Add(SelectedItemError);
                    }

                    ((RelayCommand)ApplySelectedItemCommand).RaiseCanExecuteChanged();
                }

                private void RaiseRowActionCanExecuteChanged()
                {
                    ((RelayCommand)MarkItemReadyCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)MarkItemForReviewCommand).RaiseCanExecuteChanged();
                }

                private bool ContainsItemName(string name)
                {
                    foreach (var item in Items)
                    {
                        if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                private void SetErrors(string propertyName, IReadOnlyList<string> errors)
                {
                    var changed = errors.Count == 0
                        ? _errors.Remove(propertyName)
                        : true;
                    if (errors.Count > 0)
                    {
                        _errors[propertyName] = new List<string>(errors);
                    }

                    if (changed)
                    {
                        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasErrors)));
                    }
                }

                private void ConfigureItemsView()
                {
                    ItemsView.SortDescriptions.Clear();
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(DashboardItem.Status), ListSortDirection.Ascending));
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(DashboardItem.Name), ListSortDirection.Ascending));
                    ItemsView.GroupDescriptions?.Clear();
                    ItemsView.GroupDescriptions?.Add(new PropertyGroupDescription(nameof(DashboardItem.Status)));
                    if (ItemsView is ICollectionViewLiveShaping liveShaping)
                    {
                        if (liveShaping.CanChangeLiveSorting)
                        {
                            liveShaping.IsLiveSorting = true;
                            liveShaping.LiveSortingProperties.Add(nameof(DashboardItem.Status));
                            liveShaping.LiveSortingProperties.Add(nameof(DashboardItem.Name));
                        }

                        if (liveShaping.CanChangeLiveGrouping)
                        {
                            liveShaping.IsLiveGrouping = true;
                            liveShaping.LiveGroupingProperties.Add(nameof(DashboardItem.Status));
                        }
                    }
                }

                private bool FilterItem(object item)
                {
                    if (item is not DashboardItem dashboardItem)
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(SearchText))
                    {
                        return true;
                    }

                    return dashboardItem.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                           || dashboardItem.Owner.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                           || dashboardItem.Status.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
                }

                private DashboardItem? FirstVisibleItem()
                {
                    foreach (var item in ItemsView)
                    {
                        if (item is DashboardItem dashboardItem)
                        {
                            return dashboardItem;
                        }
                    }

                    return null;
                }

                private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
                {
                    if (Equals(field, value))
                    {
                        return false;
                    }

                    field = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                    return true;
                }

                public sealed record DashboardItem(string Name, string Owner, string Status);

                public sealed record DashboardMetric(string Label, string Value, string Detail);

                public sealed record DashboardProblem(string Severity, string Source, string Message);

                public sealed class DashboardNavigationItem : INotifyPropertyChanged
                {
                    private readonly Action<DashboardNavigationItem> _select;
                    private bool _isExpanded;
                    private bool _isSelected;

                    public DashboardNavigationItem(string header, string summary, object view, int viewIndex, Action<DashboardNavigationItem> select)
                    {
                        Header = header;
                        Summary = summary;
                        View = view;
                        ViewIndex = viewIndex;
                        _select = select;
                    }

                    public event PropertyChangedEventHandler? PropertyChanged;

                    public string Header { get; }

                    public string Summary { get; }

                    public object View { get; }

                    public int ViewIndex { get; }

                    public ObservableCollection<DashboardNavigationItem> Children { get; } = new();

                    public bool IsExpanded
                    {
                        get => _isExpanded;
                        set => SetField(ref _isExpanded, value);
                    }

                    public bool IsSelected
                    {
                        get => _isSelected;
                        set
                        {
                            if (SetField(ref _isSelected, value) && value)
                            {
                                _select(this);
                            }
                        }
                    }

                    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
                    {
                        if (Equals(field, value))
                        {
                            return false;
                        }

                        field = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                        return true;
                    }
                }

                private sealed class AsyncRelayCommand : ICommand
                {
                    private readonly Func<object?, Task> _execute;
                    private readonly Predicate<object?>? _canExecute;
                    private bool _isExecuting;

                    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
                    {
                        _execute = execute;
                        _canExecute = canExecute;
                    }

                    public event EventHandler? CanExecuteChanged;

                    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

                    public async void Execute(object? parameter)
                    {
                        if (!CanExecute(parameter))
                        {
                            return;
                        }

                        try
                        {
                            _isExecuting = true;
                            RaiseCanExecuteChanged();
                            await _execute(parameter);
                        }
                        finally
                        {
                            _isExecuting = false;
                            RaiseCanExecuteChanged();
                        }
                    }

                    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                }

                private sealed class RelayCommand : ICommand
                {
                    private readonly Action<object?> _execute;
                    private readonly Predicate<object?>? _canExecute;

                    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
                    {
                        _execute = execute;
                        _canExecute = canExecute;
                    }

                    public event EventHandler? CanExecuteChanged;

                    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

                    public void Execute(object? parameter) => _execute(parameter);

                    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public sealed class DashboardItemCardTemplateSelector : DataTemplateSelector
            {
                public DataTemplate? ReadyTemplate { get; set; }

                public DataTemplate? ReviewTemplate { get; set; }

                public DataTemplate? DefaultTemplate { get; set; }

                public override DataTemplate? SelectTemplate(object item, DependencyObject container)
                {
                    if (item is MainWindowViewModel.DashboardItem dashboardItem)
                    {
                        if (dashboardItem.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                        {
                            return ReadyTemplate ?? DefaultTemplate;
                        }

                        if (dashboardItem.Status.Equals("Review", StringComparison.OrdinalIgnoreCase))
                        {
                            return ReviewTemplate ?? DefaultTemplate;
                        }
                    }

                    return DefaultTemplate ?? base.SelectTemplate(item, container);
                }
            }

            public static class DashboardFocusBehavior
            {
                public static readonly DependencyProperty FocusOnLoadedProperty =
                    DependencyProperty.RegisterAttached(
                        "FocusOnLoaded",
                        typeof(bool),
                        typeof(DashboardFocusBehavior),
                        new PropertyMetadata(false, OnFocusOnLoadedChanged));

                public static bool GetFocusOnLoaded(DependencyObject element) =>
                    (bool)element.GetValue(FocusOnLoadedProperty);

                public static void SetFocusOnLoaded(DependencyObject element, bool value) =>
                    element.SetValue(FocusOnLoadedProperty, value);

                private static void OnFocusOnLoadedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
                {
                    if (dependencyObject is not FrameworkElement element)
                    {
                        return;
                    }

                    element.Loaded -= FocusElementOnLoaded;
                    if (args.NewValue is true)
                    {
                        element.Loaded += FocusElementOnLoaded;
                    }
                }

                private static void FocusElementOnLoaded(object sender, RoutedEventArgs args)
                {
                    if (sender is FrameworkElement element)
                    {
                        element.Focus();
                    }
                }
            }

            public static class DashboardSelectionBehavior
            {
                public static readonly DependencyProperty ScrollSelectedItemIntoViewProperty =
                    DependencyProperty.RegisterAttached(
                        "ScrollSelectedItemIntoView",
                        typeof(bool),
                        typeof(DashboardSelectionBehavior),
                        new PropertyMetadata(false, OnScrollSelectedItemIntoViewChanged));

                public static bool GetScrollSelectedItemIntoView(DependencyObject element) =>
                    (bool)element.GetValue(ScrollSelectedItemIntoViewProperty);

                public static void SetScrollSelectedItemIntoView(DependencyObject element, bool value) =>
                    element.SetValue(ScrollSelectedItemIntoViewProperty, value);

                private static void OnScrollSelectedItemIntoViewChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
                {
                    if (dependencyObject is not Selector selector)
                    {
                        return;
                    }

                    WeakEventManager<Selector, SelectionChangedEventArgs>.RemoveHandler(selector, nameof(selector.SelectionChanged), OnSelectionChanged);
                    if (args.NewValue is true)
                    {
                        WeakEventManager<Selector, SelectionChangedEventArgs>.AddHandler(selector, nameof(selector.SelectionChanged), OnSelectionChanged);
                        ScrollCurrentSelectionIntoView(selector);
                    }
                }

                private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
                {
                    if (sender is Selector selector)
                    {
                        ScrollCurrentSelectionIntoView(selector);
                    }
                }

                private static void ScrollCurrentSelectionIntoView(Selector selector)
                {
                    var item = selector.SelectedItem;
                    if (item is null)
                    {
                        return;
                    }

                    selector.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (selector is DataGrid dataGrid)
                        {
                            dataGrid.ScrollIntoView(item);
                        }
                        else if (selector is ListBox listBox)
                        {
                            listBox.ScrollIntoView(item);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }

            public sealed class DashboardAdaptiveWrapPanel : Panel
            {
                public static readonly DependencyProperty MinItemWidthProperty =
                    DependencyProperty.Register(
                        nameof(MinItemWidth),
                        typeof(double),
                        typeof(DashboardAdaptiveWrapPanel),
                        new FrameworkPropertyMetadata(180d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

                public double MinItemWidth
                {
                    get => (double)GetValue(MinItemWidthProperty);
                    set => SetValue(MinItemWidthProperty, value);
                }

                protected override Size MeasureOverride(Size availableSize)
                {
                    var width = double.IsInfinity(availableSize.Width)
                        ? Math.Max(MinItemWidth, MinItemWidth * Math.Max(1, InternalChildren.Count))
                        : availableSize.Width;
                    var columnWidth = CalculateColumnWidth(width);
                    var columns = Math.Max(1, (int)Math.Floor(width / columnWidth));
                    var rowHeight = 0d;
                    var totalHeight = 0d;
                    var columnIndex = 0;

                    foreach (UIElement child in InternalChildren)
                    {
                        child.Measure(new Size(columnWidth, availableSize.Height));
                        rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                        columnIndex++;
                        if (columnIndex == columns)
                        {
                            totalHeight += rowHeight;
                            rowHeight = 0d;
                            columnIndex = 0;
                        }
                    }

                    if (columnIndex > 0)
                    {
                        totalHeight += rowHeight;
                    }

                    return new Size(width, totalHeight);
                }

                protected override Size ArrangeOverride(Size finalSize)
                {
                    var columnWidth = CalculateColumnWidth(finalSize.Width);
                    var columns = Math.Max(1, (int)Math.Floor(finalSize.Width / columnWidth));
                    var x = 0d;
                    var y = 0d;
                    var rowHeight = 0d;
                    var columnIndex = 0;

                    foreach (UIElement child in InternalChildren)
                    {
                        rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                        child.Arrange(new Rect(x, y, columnWidth, child.DesiredSize.Height));
                        columnIndex++;
                        if (columnIndex == columns)
                        {
                            x = 0d;
                            y += rowHeight;
                            rowHeight = 0d;
                            columnIndex = 0;
                        }
                        else
                        {
                            x += columnWidth;
                        }
                    }

                    return finalSize;
                }

                private double CalculateColumnWidth(double availableWidth)
                {
                    var safeMin = Math.Max(1d, MinItemWidth);
                    if (double.IsInfinity(availableWidth) || availableWidth <= safeMin)
                    {
                        return safeMin;
                    }

                    var columns = Math.Max(1, (int)Math.Floor(availableWidth / safeMin));
                    return availableWidth / columns;
                }
            }

            public sealed class DashboardBindingProxy : Freezable
            {
                public static readonly DependencyProperty DataProperty =
                    DependencyProperty.Register(nameof(Data), typeof(object), typeof(DashboardBindingProxy), new UIPropertyMetadata(null));

                public object? Data
                {
                    get => GetValue(DataProperty);
                    set => SetValue(DataProperty, value);
                }

                protected override Freezable CreateInstanceCore() => new DashboardBindingProxy();
            }

            public sealed class OverviewDashboardViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
            {
                private readonly MainWindowViewModel _owner;

                public OverviewDashboardViewModel(MainWindowViewModel owner)
                {
                    _owner = owner;
                    PropertyChangedEventManager.AddHandler(_owner, OnOwnerPropertyChanged, string.Empty);
                    WeakEventManager<INotifyDataErrorInfo, DataErrorsChangedEventArgs>.AddHandler(_owner, nameof(_owner.ErrorsChanged), OnOwnerErrorsChanged);
                }

                public event PropertyChangedEventHandler? PropertyChanged;

                public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

                public ICollectionView ItemsView => _owner.ItemsView;

                public ObservableCollection<MainWindowViewModel.DashboardMetric> Metrics => _owner.Metrics;

                public string SearchText
                {
                    get => _owner.SearchText;
                    set => _owner.SearchText = value;
                }

                public MainWindowViewModel.DashboardItem? SelectedItem
                {
                    get => _owner.SelectedItem;
                    set => _owner.SelectedItem = value;
                }

                public string NewItemName
                {
                    get => _owner.NewItemName;
                    set => _owner.NewItemName = value;
                }

                public string NewItemError => _owner.NewItemError;

                public ICommand AddItemCommand => _owner.AddItemCommand;

                public ICommand RemoveSelectedItemCommand => _owner.RemoveSelectedItemCommand;

                public ICommand MarkItemReadyCommand => _owner.MarkItemReadyCommand;

                public ICommand MarkItemForReviewCommand => _owner.MarkItemForReviewCommand;

                public bool HasErrors => _owner.HasErrors;

                public IEnumerable GetErrors(string? propertyName) => _owner.GetErrors(propertyName);

                private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
                    PropertyChanged?.Invoke(this, args);

                private void OnOwnerErrorsChanged(object? sender, DataErrorsChangedEventArgs args) =>
                    ErrorsChanged?.Invoke(this, args);
            }

            public sealed class ActivityDashboardViewModel
            {
                private readonly MainWindowViewModel _owner;

                public ActivityDashboardViewModel(MainWindowViewModel owner)
                {
                    _owner = owner;
                }

                public ObservableCollection<string> Activity => _owner.Activity;
            }

            public sealed class SettingsDashboardViewModel
            {
                public SettingsDashboardViewModel()
                {
                    SettingsNotes.Add("Use view-model properties for settings so the UI can bind, validate, and test them.");
                    SettingsNotes.Add("Keep shell navigation independent from each view's internal layout.");
                    SettingsNotes.Add("Move long-running work behind async commands so the window remains responsive.");
                }

                public string Title { get; } = "Settings";

                public string Description { get; } = "This composed settings view is a placeholder for real application configuration.";

                public ObservableCollection<string> SettingsNotes { get; } = new();
            }

            public static class DashboardDesignData
            {
                public static MainWindowViewModel DesignViewModel => new();

                public static MainWindowViewModel.DashboardItem DesignItem { get; } =
                    new("Design preview item", "Designer", "Review");
            }

            public interface IDashboardDialogService
            {
                bool Confirm(DashboardDialogRequest request);
            }

            public sealed record DashboardDialogRequest(
                string Title,
                string Message,
                string PrimaryText = "OK",
                string CancelText = "Cancel");

            public sealed class NullDashboardDialogService : IDashboardDialogService
            {
                public bool Confirm(DashboardDialogRequest request) => true;
            }

            public interface IDashboardLayoutStateService
            {
                DashboardLayoutState Load();

                void Save(DashboardLayoutState state);
            }

            public sealed record DashboardLayoutState(
                double NavigationColumnWidth,
                double DetailsColumnWidth,
                double OutputPanelHeight,
                int SelectedOutputPanelIndex,
                double WindowLeft,
                double WindowTop,
                double WindowWidth,
                double WindowHeight)
            {
                public static DashboardLayoutState Default { get; } = new(220d, 280d, 160d, 0, double.NaN, double.NaN, 980d, 620d);

                public bool HasWindowBounds =>
                    !double.IsNaN(WindowLeft)
                    && !double.IsNaN(WindowTop)
                    && WindowWidth > 0d
                    && WindowHeight > 0d;
            }

            public sealed class NullDashboardLayoutStateService : IDashboardLayoutStateService
            {
                public DashboardLayoutState Load() => DashboardLayoutState.Default;

                public void Save(DashboardLayoutState state)
                {
                }
            }

            public interface IDashboardThemeService
            {
                void ApplyTheme(DashboardThemePalette palette);
            }

            public sealed class NullDashboardThemeService : IDashboardThemeService
            {
                public void ApplyTheme(DashboardThemePalette palette)
                {
                }
            }

            public sealed record DashboardThemePalette(
                Color Header,
                Color HeaderForeground,
                Color SubtleForeground,
                Color Accent,
                Color AccentHover)
            {
                public static DashboardThemePalette Dark { get; } = new(
                    Color.FromRgb(0x20, 0x2A, 0x36),
                    Color.FromRgb(0xFF, 0xFF, 0xFF),
                    Color.FromRgb(0xC8, 0xD2, 0xDF),
                    Color.FromRgb(0x1F, 0x6F, 0xEB),
                    Color.FromRgb(0x2F, 0x81, 0xF7));

                public static DashboardThemePalette Light { get; } = new(
                    Color.FromRgb(0xF6, 0xF8, 0xFA),
                    Color.FromRgb(0x1F, 0x23, 0x28),
                    Color.FromRgb(0x57, 0x66, 0x75),
                    Color.FromRgb(0x09, 0x68, 0xDA),
                    Color.FromRgb(0x1F, 0x6F, 0xEB));
            }

            public sealed class DashboardSelectionSummaryConverter : IMultiValueConverter
            {
                public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
                {
                    var name = values.Length > 0 ? values[0]?.ToString() : string.Empty;
                    var status = values.Length > 1 ? values[1]?.ToString() : string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return "No item selected";
                    }

                    return string.IsNullOrWhiteSpace(status)
                        ? name
                        : $"{name} - {status}";
                }

                public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
                {
                    var results = new object[targetTypes.Length];
                    Array.Fill(results, Binding.DoNothing);
                    return results;
                }
            }

            public sealed class DashboardStatusBrushConverter : IValueConverter
            {
                public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
                {
                    var status = value?.ToString() ?? string.Empty;
                    if (status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                    {
                        return Brushes.SeaGreen;
                    }

                    if (status.Equals("Review", StringComparison.OrdinalIgnoreCase))
                    {
                        return Brushes.DarkOrange;
                    }

                    if (status.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                    {
                        return Brushes.SlateBlue;
                    }

                    return Brushes.DimGray;
                }

                public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
            }
            """;
    }

    private static string? ExtractXamlClassName(string content)
    {
        var match = Regex.Match(
            content,
            @"x:Class\s*=\s*""(?<name>[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    private static string? ExtractCSharpNamespaceName(string content)
    {
        var match = Regex.Match(
            content,
            """\bnamespace\s+(?<name>[A-Za-z_][\w.]*)\s*(?:;|\{)""",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    private static bool IsSimpleWpfProgramGoal(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return false;
        }

        return (MentionsAny(goal, "wpf", "xaml", "desktop window", "desktop app", "windowed app") || IsWpfComplexWindowGoal(goal))
               && (IsWpfCounterGoal(goal)
                   || IsWpfCalculatorGoal(goal)
                   || IsWpfGreetingGoal(goal)
                   || IsWpfTodoGoal(goal)
                   || IsWpfComplexWindowGoal(goal)
                   || IsWpfHelloGoal(goal)
                   || MentionsAny(goal, "button", "window", "screen"));
    }

    private static bool NeedsWpfCodeBehind(string goal) =>
        IsWpfCounterGoal(goal)
        || IsWpfCalculatorGoal(goal)
        || IsWpfGreetingGoal(goal)
        || IsWpfTodoGoal(goal)
        || IsWpfComplexWindowGoal(goal);

    private static string ClassifyWpfStarterNote(string goal, string fileName)
    {
        var shape = IsWpfComplexWindowGoal(goal)
            ? "WPF complex-dashboard"
            : IsWpfTodoGoal(goal)
                ? "WPF todo-list"
                : IsWpfCalculatorGoal(goal)
                    ? "WPF calculator"
                    : IsWpfGreetingGoal(goal)
                        ? "WPF greeting-form"
                        : IsWpfCounterGoal(goal)
                            ? "WPF counter"
                            : "WPF hello";
        return $"{shape} {fileName} starter recipe.";
    }

    private static bool IsWpfCounterGoal(string goal) =>
        MentionsAny(goal, "counter", "count", "increment", "increase")
        && MentionsAny(goal, "button", "click", "press");

    private static bool IsWpfCalculatorGoal(string goal) =>
        MentionsAny(goal, "calculator", "calculate", "math", "arithmetic")
        || (MentionsAny(goal, "add", "subtract", "multiply", "divide")
            && MentionsAny(goal, "number", "numbers", "operation"));

    private static bool IsWpfGreetingGoal(string goal) =>
        MentionsAny(goal, "greeting", "say hello", "greeter", "ask for a name", "name textbox")
        || (MentionsAny(goal, "hello") && MentionsAny(goal, "name", "textbox", "text box", "input"));

    private static bool IsWpfTodoGoal(string goal) =>
        MentionsAny(goal, "todo", "to-do", "task list", "tasks", "checklist")
        && MentionsAny(goal, "add", "remove", "list", "selected", "button");

    private static bool IsWpfComplexWindowGoal(string goal) =>
        MentionsAny(goal, "dashboard", "control center", "complex window", "advanced window", "multi panel", "multi-pane", "split pane", "master detail", "details pane", "navigation shell", "management window", "manager window", "data entry")
        && MentionsAny(goal, "wpf", "xaml", "desktop", "window", "screen", "app", "layout", "grid", "tabs", "data grid");

    private static bool IsWpfHelloGoal(string goal) =>
        MentionsAny(goal, "hello world", "hello-world", "says hello", "display hello");

    private static bool IsBuildArtifactPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }
}
