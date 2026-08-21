using System.Globalization;
using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void LoadsEnglishAndSpanishAndRemovesGlobalLanguageOption()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            L10n.SetCulture("en");
            Assert.Equal("Profiles and credentials", L10n.T("HelpProfilesTitle"));
            Assert.Equal("(no credentials)", L10n.T("LabelNoCredentials"));

            string[] remaining = L10n.Configure(
                ["sample", "--LANG", "es"]);

            Assert.Equal(["sample"], remaining);
            Assert.Equal("Perfiles y credenciales", L10n.T("HelpProfilesTitle"));
            Assert.Equal("(sin credenciales)", L10n.T("LabelNoCredentials"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
