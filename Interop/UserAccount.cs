using System.Runtime.InteropServices;
using System.Text;

namespace DevShell.Interop;

/// <summary>
/// Who is signed in, spelled the way Windows spells it - "Администратор" rather than the bare
/// account name, because that is what the Start menu is expected to show.
/// </summary>
internal static class UserAccount
{
    /// <summary>EXTENDED_NAME_FORMAT.NameDisplay.</summary>
    private const int NameDisplay = 3;

    [DllImport("secur32.dll", EntryPoint = "GetUserNameExW", CharSet = CharSet.Unicode)]
    private static extern bool GetUserNameEx(int format, StringBuilder name, ref int size);

    public static string DisplayName
    {
        get
        {
            try
            {
                var size = 257;
                var buffer = new StringBuilder(size);
                if (GetUserNameEx(NameDisplay, buffer, ref size) && buffer.Length > 0)
                {
                    return buffer.ToString();
                }
            }
            catch (Exception ex)
            {
                App.Log("UserAccount.DisplayName", ex);
            }

            // A machine without secur32 (or a name Windows will not resolve) still has an account.
            return Environment.UserName;
        }
    }
}
