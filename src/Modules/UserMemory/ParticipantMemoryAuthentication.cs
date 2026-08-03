using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.UserMemory;

internal interface IParticipantMemoryAuthenticationProvider
{
    Task<ParticipantMemoryAuthenticationReceipt?> AuthenticateAsync(
        string principalParticipantReference,
        IReadOnlyList<string> operations,
        string reason,
        CancellationToken cancellationToken);

    bool IsCurrentBinding(
        ParticipantMemoryAuthenticationReceipt receipt,
        DateTimeOffset now);
}

internal interface ILocalParticipantCredentialVerifier
{
    bool VerifyCurrentWindowsPrincipal(string reason);
}

/// <summary>
/// Production independent-factor boundary. Recognition and presence never reach this
/// issuer: Windows asks for a credential, LogonUser validates it, and the resulting
/// token must carry the same SID as the current desktop process.
/// </summary>
internal sealed class WindowsCredentialParticipantAuthenticationProvider(
    IActiveUserSession activeUsers,
    ParticipantMemoryReceiptAuthority receipts,
    ILocalParticipantCredentialVerifier verifier) : IParticipantMemoryAuthenticationProvider
{
    private readonly IActiveUserSession _activeUsers = activeUsers
        ?? throw new ArgumentNullException(nameof(activeUsers));
    private readonly ParticipantMemoryReceiptAuthority _receipts = receipts
        ?? throw new ArgumentNullException(nameof(receipts));
    private readonly ILocalParticipantCredentialVerifier _verifier = verifier
        ?? throw new ArgumentNullException(nameof(verifier));

    public Task<ParticipantMemoryAuthenticationReceipt?> AuthenticateAsync(
        string principalParticipantReference,
        IReadOnlyList<string> operations,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = principalParticipantReference?.Trim() ?? string.Empty;
        var before = _activeUsers.CaptureSelectionSnapshot();
        if (!IsSoleRegisteredOwner(before, principal))
        {
            return Task.FromResult<ParticipantMemoryAuthenticationReceipt?>(null);
        }

        if (!_verifier.VerifyCurrentWindowsPrincipal(reason))
        {
            return Task.FromResult<ParticipantMemoryAuthenticationReceipt?>(null);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var after = _activeUsers.CaptureSelectionSnapshot();
        if (!IsSoleRegisteredOwner(after, principal))
        {
            return Task.FromResult<ParticipantMemoryAuthenticationReceipt?>(null);
        }

        return Task.FromResult<ParticipantMemoryAuthenticationReceipt?>(
            _receipts.IssueAuthentication(
                principal,
                ParticipantMemoryAuthenticationKind.LocalCredential,
                operations,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(2)));
    }

    public bool IsCurrentBinding(
        ParticipantMemoryAuthenticationReceipt receipt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.Kind == ParticipantMemoryAuthenticationKind.LocalCredential
            && _receipts.IsIssued(receipt)
            && receipt.IsCurrent(now)
            && IsSoleRegisteredOwner(
                _activeUsers.CaptureSelectionSnapshot(),
                receipt.PrincipalParticipantReference);
    }

    private bool IsSoleRegisteredOwner(
        ActiveUserSelectionSnapshot selection,
        string principal)
    {
        try
        {
            if (!selection.IsResolved)
            {
                return false;
            }
            var selected = selection.SelectedUser!.Normalize();
            if (selected.IsTestProfile
                || !string.Equals(selected.StableId, principal, StringComparison.Ordinal))
            {
                return false;
            }

            var registered = _activeUsers.AvailableUsers
                .Select(user => user.Normalize())
                .Where(user => !user.IsTestProfile)
                .DistinctBy(user => user.StableId, StringComparer.Ordinal)
                .ToArray();
            return registered.Length == 1
                && string.Equals(
                    registered[0].StableId,
                    principal,
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class WindowsCredentialVerifier : ILocalParticipantCredentialVerifier
{
    private const uint CredUiWinGeneric = 0x00000001;
    private const uint CredUiWinEnumerateCurrentUser = 0x00000200;
    private const uint ErrorCancelled = 1223;
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    public bool VerifyCurrentWindowsPrincipal(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var info = new CredUiInfo
        {
            Size = Marshal.SizeOf<CredUiInfo>(),
            CaptionText = "Confirm participant-memory action",
            MessageText = string.IsNullOrWhiteSpace(reason)
                ? "Enter the current Windows account credential to continue."
                : reason.Trim()
        };
        uint authenticationPackage = 0;
        var save = false;
        var result = CredUIPromptForWindowsCredentials(
            ref info,
            0,
            ref authenticationPackage,
            IntPtr.Zero,
            0,
            out var authenticationBuffer,
            out var authenticationBufferSize,
            ref save,
            CredUiWinGeneric | CredUiWinEnumerateCurrentUser);
        if (result == ErrorCancelled)
        {
            return false;
        }
        if (result != 0)
        {
            throw new Win32Exception((int)result, "Windows credential verification could not start.");
        }

        try
        {
            return ValidateBuffer(authenticationBuffer, authenticationBufferSize);
        }
        finally
        {
            if (authenticationBuffer != IntPtr.Zero)
            {
                ZeroMemory(authenticationBuffer, authenticationBufferSize);
                Marshal.FreeCoTaskMem(authenticationBuffer);
            }
        }
    }

    private static bool ValidateBuffer(IntPtr buffer, uint bufferSize)
    {
        uint userLength = 0;
        uint domainLength = 0;
        uint passwordLength = 0;
        _ = CredUnPackAuthenticationBuffer(
            0,
            buffer,
            bufferSize,
            null,
            ref userLength,
            null,
            ref domainLength,
            null,
            ref passwordLength);
        if (userLength == 0 || passwordLength == 0)
        {
            return false;
        }

        var user = new StringBuilder((int)userLength);
        var domain = new StringBuilder((int)Math.Max(domainLength, 1));
        var password = new StringBuilder((int)passwordLength);
        try
        {
            if (!CredUnPackAuthenticationBuffer(
                    0,
                    buffer,
                    bufferSize,
                    user,
                    ref userLength,
                    domain,
                    ref domainLength,
                    password,
                    ref passwordLength))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows did not return a usable credential.");
            }

            var passwordCharacters = new char[password.Length + 1];
            password.CopyTo(0, passwordCharacters, 0, password.Length);
            try
            {
                unsafe
                {
                    fixed (char* passwordPointer = passwordCharacters)
                    {
                        if (!LogonUser(
                                user.ToString(),
                                domain.Length == 0 ? null : domain.ToString(),
                                (IntPtr)passwordPointer,
                                Logon32LogonInteractive,
                                Logon32ProviderDefault,
                                out var token))
                        {
                            return false;
                        }
                        using (token)
                        using (var authenticated = new WindowsIdentity(token.DangerousGetHandle()))
                        using (var current = WindowsIdentity.GetCurrent())
                        {
                            return authenticated.User is not null
                                && current.User is not null
                                && authenticated.User.Equals(current.User);
                        }
                    }
                }
            }
            finally
            {
                Array.Clear(passwordCharacters);
            }
        }
        finally
        {
            for (var index = 0; index < password.Length; index++)
            {
                password[index] = '\0';
            }
            password.Clear();
        }
    }

    private static void ZeroMemory(IntPtr buffer, uint size)
    {
        for (nuint offset = 0; offset < size; offset++)
        {
            Marshal.WriteByte(buffer, checked((int)offset), 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public int Size;
        public IntPtr ParentWindow;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MessageText;
        [MarshalAs(UnmanagedType.LPWStr)] public string? CaptionText;
        public IntPtr Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern uint CredUIPromptForWindowsCredentials(
        ref CredUiInfo credentialUiInfo,
        uint authenticationError,
        ref uint authenticationPackage,
        IntPtr inputAuthenticationBuffer,
        uint inputAuthenticationBufferSize,
        out IntPtr outputAuthenticationBuffer,
        out uint outputAuthenticationBufferSize,
        ref bool save,
        uint flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBuffer(
        uint flags,
        IntPtr authenticationBuffer,
        uint authenticationBufferSize,
        StringBuilder? userName,
        ref uint maximumUserName,
        StringBuilder? domainName,
        ref uint maximumDomainName,
        StringBuilder? password,
        ref uint maximumPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string userName,
        string? domain,
        IntPtr password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle token);
}
