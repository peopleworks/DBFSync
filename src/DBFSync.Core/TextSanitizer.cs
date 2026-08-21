using System.Text;

namespace PeopleWorks.DBFSync;

public static class TextSanitizer
{
    private static readonly Dictionary<char, char> OemMojibake = new()
    {
        ['ß'] = 'á',
        ['Θ'] = 'é',
        ['φ'] = 'í',
        ['≤'] = 'ó',
        ['·'] = 'ú',
        ['±'] = 'ñ',
        ['┴'] = 'Á',
        ['╔'] = 'É',
        ['═'] = 'Í',
        ['╙'] = 'Ó',
        ['┌'] = 'Ú',
        ['╤'] = 'Ñ',
        ['ⁿ'] = 'ü'
    };

    static TextSanitizer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Sanitize(string value)
    {
        string result = value.Normalize(NormalizationForm.FormC);
        if (result.Contains('Ã') || result.Contains('Â'))
        {
            Encoding windows1252 = Encoding.GetEncoding(
                1252,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            try
            {
                string candidate = Encoding.UTF8.GetString(windows1252.GetBytes(result));
                if (!candidate.Contains('\uFFFD'))
                    result = candidate;
            }
            catch (EncoderFallbackException)
            {
                // The conservative OEM mapping below may still repair the value.
            }
        }

        if (result.Any(OemMojibake.ContainsKey))
            result = string.Concat(result.Select(c => OemMojibake.GetValueOrDefault(c, c)));
        return result.Normalize(NormalizationForm.FormC);
    }
}
