using Miniscript;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

#nullable enable

namespace Uplink2.Runtime.MiniScript;

/// <summary>Registers and injects project-specific crypto intrinsics into MiniScript interpreters.</summary>
internal static class MiniScriptCryptoIntrinsics
{
    private const string UnixTimeIntrinsicName = "uplink_crypto_unixTime";
    private const string OtpNowIntrinsicName = "uplink_crypto_otpNow";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static readonly object registrationSync = new();
    private static bool isRegistered;

    /// <summary>Ensures custom crypto intrinsics are registered exactly once per process.</summary>
    internal static void EnsureRegistered()
    {
        lock (registrationSync)
        {
            if (isRegistered)
            {
                return;
            }

            RegisterUnixTimeIntrinsic();
            RegisterOtpNowIntrinsic();
            isRegistered = true;
        }
    }

    /// <summary>Injects crypto module globals into a compiled interpreter instance.</summary>
    internal static void InjectCryptoModule(Interpreter interpreter, Func<double>? unixTimeProvider = null)
    {
        if (interpreter is null)
        {
            throw new ArgumentNullException(nameof(interpreter));
        }

        EnsureRegistered();
        interpreter.Compile();
        if (interpreter.vm is null)
        {
            return;
        }

        var cryptoModule = new ValMap
        {
            userData = new CryptoModuleState(unixTimeProvider ?? DefaultUnixTimeProvider),
        };
        cryptoModule["unixTime"] = Intrinsic.GetByName(UnixTimeIntrinsicName).GetFunc();
        cryptoModule["otpNow"] = Intrinsic.GetByName(OtpNowIntrinsicName).GetFunc();
        interpreter.SetGlobalValue("crypto", cryptoModule);
    }

    private static void RegisterUnixTimeIntrinsic()
    {
        if (Intrinsic.GetByName(UnixTimeIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(UnixTimeIntrinsicName);
        intrinsic.code = (context, _) =>
        {
            return new Intrinsic.Result(ResolveUnixTime(context));
        };
    }

    private static void RegisterOtpNowIntrinsic()
    {
        if (Intrinsic.GetByName(OtpNowIntrinsicName) is not null)
        {
            return;
        }

        var intrinsic = Intrinsic.Create(OtpNowIntrinsicName);
        intrinsic.AddParam("pairId");
        intrinsic.AddParam("nowMs");
        intrinsic.AddParam("stepMs", 30000);
        intrinsic.AddParam("digits", 6);
        intrinsic.code = (context, _) =>
        {
            var pairId = context.GetLocalString("pairId");
            var nowMs = context.GetLocalDouble("nowMs");
            var stepMs = context.GetLocalDouble("stepMs", 30000);
            var digits = context.GetLocalInt("digits", 6);
            var otp = GenerateTotp(pairId, nowMs, stepMs, digits);
            return new Intrinsic.Result(otp);
        };
    }

    private static double ResolveUnixTime(TAC.Context context)
    {
        if (context.self is ValMap selfMap &&
            selfMap.userData is CryptoModuleState state)
        {
            return state.UnixTimeProvider();
        }

        return DefaultUnixTimeProvider();
    }

    private static double DefaultUnixTimeProvider()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    private static string GenerateTotp(string? pairId, double nowMs, double stepMs, int digits)
    {
        if (string.IsNullOrWhiteSpace(pairId))
        {
            throw new RuntimeException("crypto.otpNow: pairId is required.");
        }

        if (stepMs <= 0)
        {
            throw new RuntimeException("crypto.otpNow: stepMs must be greater than 0.");
        }

        if (digits < 1 || digits > 10)
        {
            throw new RuntimeException("crypto.otpNow: digits must be in range 1..10.");
        }

        byte[] secretBytes;
        try
        {
            secretBytes = DecodeBase32Secret(pairId);
        }
        catch (FormatException ex)
        {
            throw new RuntimeException("crypto.otpNow: invalid pairId (base32 expected).", ex);
        }

        var counter = (long)Math.Floor(nowMs / stepMs);
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        byte[] hash;
        using (var hmac = new HMACSHA1(secretBytes))
        {
            hash = hmac.ComputeHash(counterBytes.ToArray());
        }

        var offset = hash[^1] & 0x0F;
        var binaryCode = ((hash[offset] & 0x7F) << 24) |
                         ((hash[offset + 1] & 0xFF) << 16) |
                         ((hash[offset + 2] & 0xFF) << 8) |
                         (hash[offset + 3] & 0xFF);

        long modulus = 1;
        for (var index = 0; index < digits; index++)
        {
            modulus *= 10;
        }

        var otp = binaryCode % modulus;
        return otp.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    private static byte[] DecodeBase32Secret(string value)
    {
        var normalized = value
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant();
        if (normalized.Length == 0)
        {
            throw new FormatException("empty base32 input");
        }

        var bytes = new List<byte>(normalized.Length * 5 / 8 + 1);
        var bitBuffer = 0;
        var bitsInBuffer = 0;

        foreach (var ch in normalized)
        {
            var charIndex = Base32Alphabet.IndexOf(ch);
            if (charIndex < 0)
            {
                throw new FormatException("invalid base32 character");
            }

            bitBuffer = (bitBuffer << 5) | charIndex;
            bitsInBuffer += 5;

            while (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                bytes.Add((byte)((bitBuffer >> bitsInBuffer) & 0xFF));
            }
        }

        if (bytes.Count == 0)
        {
            throw new FormatException("base32 decoded to empty payload");
        }

        return bytes.ToArray();
    }

    private sealed class CryptoModuleState
    {
        internal CryptoModuleState(Func<double> unixTimeProvider)
        {
            UnixTimeProvider = unixTimeProvider;
        }

        internal Func<double> UnixTimeProvider { get; }
    }
}
