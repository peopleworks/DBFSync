using System.Text;

namespace PeopleWorks.DBFSync.Cli;

internal static class PasswordPrompt
{
    public static string Read(string prompt)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException(
                L10n.T("NonInteractivePassword"));

        Console.Write(prompt);
        var password = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                    password.Length--;
                continue;
            }
            if (key.Key == ConsoleKey.Escape)
                throw new OperationCanceledException(L10n.T("PasswordCancelled"));
            if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }

        return password.Length > 0
            ? password.ToString()
            : throw new ArgumentException(L10n.T("PasswordEmpty"));
    }
}
