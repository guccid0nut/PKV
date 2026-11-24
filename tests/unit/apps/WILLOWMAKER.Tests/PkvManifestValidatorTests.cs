// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using FluentAssertions;
using NEXUS.Contracts.Cdn;
using WILLOWMAKER.Core.Models;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class PkvManifestValidatorTests
{
    private static (Manifest Manifest, ECDsa Key, string PubXHex, string PubYHex) CreateValidPkvV5Manifest()
    {
        ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters ecParams = ecdsa.ExportParameters(includePrivateParameters: true);
        string pubXHex = Convert.ToHexString(ecParams.Q.X!);
        string pubYHex = Convert.ToHexString(ecParams.Q.Y!);

        Manifest manifest = new()
        {
            Protocol = Manifest.PkvProtocolV5,
            Version = "5.0.1.0",
            Mode = "overlay",
            HashAlgorithm = "SHA-256",
            ObjectLayout = "sha256",
            Sequence = 1,
            ExcludeFromSource = [],
            ExcludeFromTarget = [],
            Files = new Dictionary<string, ManifestEntry>
            {
                ["games/client/override/test.txt"] = new()
                {
                    Size = 12,
                    Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                }
            }
        };

        manifest = manifest with
        {
            Signature = ManifestSignatureVerifier.SignManifest(manifest, ecdsa)
        };

        return (manifest, ecdsa, pubXHex, pubYHex);
    }

    [Fact]
    public void Verify_WhenManifestIsNull_FailsWithNullManifestCode()
    {
        // protects: Fail-closed rejection of null manifests before any dereferencing or execution.
        // fails_when: Null manifest returns valid result or throws NullReferenceException.
        // seam: PkvManifestValidator.Verify.
        PkvValidationResult result = PkvManifestValidator.Verify(null);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.NullManifest);
        result.Reason.Should().Contain("null");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy-pk")]
    [InlineData("pkv-v4")]
    [InlineData("pkv-v6")]
    public void Verify_WhenProtocolIsNotPkvV5_FailsWithMissingOrUnsupportedProtocol(string? protocol)
    {
        // protects: Strict requirement for "protocol": "pkv-v5" identifying the Project Kongor Vault protocol.
        // fails_when: Non-PKV or legacy protocol strings pass verification.
        // seam: PkvManifestValidator.Verify and Manifest.IsPkvV5Protocol.
        var (manifest, key, pubX, pubY) = CreateValidPkvV5Manifest();
        manifest = manifest with { Protocol = protocol };
        manifest = manifest with { Signature = ManifestSignatureVerifier.SignManifest(manifest, key) };

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.MissingOrUnsupportedProtocol);
        result.Reason.Should().Contain("PKV v5");
    }

    [Theory]
    [InlineData("4.10.1")]
    [InlineData("4.0.0.0")]
    [InlineData("3.9.5")]
    [InlineData("0.2.0")]
    public void Verify_WhenVersionIsPreV5_FailsWithLegacyPreV5Version(string version)
    {
        // protects: Rejection of legacy 4.x or earlier packages from foreign or legacy master servers.
        // fails_when: Pre-v5 major versions pass verification.
        // seam: PkvManifestValidator.Verify and IsSupportedMajorVersion.
        var (manifest, key, pubX, pubY) = CreateValidPkvV5Manifest();
        manifest = manifest with { Version = version };
        manifest = manifest with { Signature = ManifestSignatureVerifier.SignManifest(manifest, key) };

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.LegacyPreV5Version);
        result.Reason.Should().Contain("major version < 5");
    }

    [Theory]
    [InlineData("mirror")]
    [InlineData("full")]
    [InlineData("")]
    public void Verify_WhenModeIsNotOverlay_FailsWithLegacyMirrorModeForbidden(string mode)
    {
        // protects: Rejection of destructive mirror mode manifests capable of purging local client files.
        // fails_when: Mirror mode or non-overlay manifests pass verification.
        // seam: PkvManifestValidator.Verify and Manifest.IsOverlay.
        var (manifest, key, pubX, pubY) = CreateValidPkvV5Manifest();
        manifest = manifest with { Mode = mode };
        manifest = manifest with { Signature = ManifestSignatureVerifier.SignManifest(manifest, key) };

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.LegacyMirrorModeForbidden);
        result.Reason.Should().Contain("Only 'overlay' mode is allowed");
    }

    [Theory]
    [InlineData("flat")]
    [InlineData(null)]
    [InlineData("tree")]
    public void Verify_WhenObjectLayoutIsNotSha256_FailsWithNonCasObjectLayout(string? objectLayout)
    {
        // protects: Invariant requiring Content-Addressable Storage (CAS) objects layout for all PKV v5 packages.
        // fails_when: Non-CAS object layouts pass verification.
        // seam: PkvManifestValidator.Verify and Manifest.UsesSha256ObjectLayout.
        var (manifest, key, pubX, pubY) = CreateValidPkvV5Manifest();
        manifest = manifest with { ObjectLayout = objectLayout };
        manifest = manifest with { Signature = ManifestSignatureVerifier.SignManifest(manifest, key) };

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.NonCasObjectLayout);
        result.Reason.Should().Contain("content-addressable storage");
    }

    [Theory]
    [InlineData("MD5")]
    [InlineData("SHA1")]
    [InlineData("CRC32")]
    public void Verify_WhenHashAlgorithmIsNotSha256_FailsWithLegacyHashAlgorithm(string hashAlgorithm)
    {
        // protects: Invariant requiring collision-resistant SHA-256 integrity hashes for all manifest entries.
        // fails_when: Weak or legacy hash algorithms pass verification.
        // seam: PkvManifestValidator.Verify.
        var (manifest, key, pubX, pubY) = CreateValidPkvV5Manifest();
        manifest = manifest with { HashAlgorithm = hashAlgorithm };
        manifest = manifest with { Signature = ManifestSignatureVerifier.SignManifest(manifest, key) };

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.LegacyHashAlgorithm);
        result.Reason.Should().Contain("SHA-256");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Verify_WhenSequenceIsNonPositive_FailsWithMissingPkvSequence(long sequence)
    {
        // protects: Monotonic sequence tracking for PKV v5 incremental updates.
        // fails_when: Sequence <= 0 passes verification.
        // seam: PkvManifestValidator.Verify.
        var (manifest, key, pubX, pubY) = CreateValidPkvV5Manifest();
        manifest = manifest with { Sequence = sequence };
        manifest = manifest with { Signature = ManifestSignatureVerifier.SignManifest(manifest, key) };

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.MissingPkvSequence);
        result.Reason.Should().Contain("sequence");
    }

    [Fact]
    public void Verify_WhenSignatureMissingAndRequired_FailsWithUntrustedOrMissingSignature()
    {
        // protects: Cryptographic signature enforcement preventing unsigned payload execution.
        // fails_when: Unsigned manifest passes when requireSignature is true.
        // seam: PkvManifestValidator.Verify.
        var (manifest, _, pubX, pubY) = CreateValidPkvV5Manifest();
        manifest = manifest with { Signature = null };

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.UntrustedOrMissingSignature);
        result.Reason.Should().Contain("signature");
    }

    [Fact]
    public void Verify_WhenSignatureIsForged_FailsWithUntrustedOrMissingSignature()
    {
        // protects: Rejection of tampered manifests or signatures produced by unauthorized private keys.
        // fails_when: Signature produced by different key passes verification against expected public key.
        // seam: PkvManifestValidator.Verify and ManifestSignatureVerifier.VerifyManifest.
        var (manifest, _, pubX, pubY) = CreateValidPkvV5Manifest();

        using ECDsa attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        manifest = manifest with { Signature = ManifestSignatureVerifier.SignManifest(manifest, attackerKey) };

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeFalse();
        result.FailureCode.Should().Be(PkvValidationFailureCode.UntrustedOrMissingSignature);
    }

    [Fact]
    public void Verify_WhenValidPkvV5Manifest_Succeeds()
    {
        // protects: End-to-end success path for valid PKV v5 manifests signed by authorized keys.
        // fails_when: Compliant manifest is erroneously rejected.
        // seam: PkvManifestValidator.Verify.
        var (manifest, key, pubX, pubY) = CreateValidPkvV5Manifest();

        PkvValidationResult result = PkvManifestValidator.Verify(manifest, requireSignature: true, pubX, pubY);

        result.IsValid.Should().BeTrue();
        result.FailureCode.Should().Be(PkvValidationFailureCode.None);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void BuildCanonicalTranscript_IncludesPkvV5ProtocolAndHeader()
    {
        // protects: Canonical signature transcript format parity between publisher script and C# verifier.
        // fails_when: Transcript header is not PKV-CDN-MANIFEST-V5 or protocol=pkv-v5 is missing.
        // seam: ManifestSignatureVerifier.BuildCanonicalTranscript.
        var (manifest, _, _, _) = CreateValidPkvV5Manifest();

        string transcript = System.Text.Encoding.UTF8.GetString(ManifestSignatureVerifier.BuildCanonicalTranscript(manifest));

        transcript.Should().StartWith("PKV-CDN-MANIFEST-V5\n");
        transcript.Should().Contain("protocol=pkv-v5\n");
        transcript.Should().Contain("mode=overlay\n");
        transcript.Should().Contain("objectLayout=sha256\n");
    }

    [Fact]
    public void EndpointPairRegistry_IsKongorNetDisabledByDefault_AndGreysOutDropdownOptions()
    {
        // protects: Default exclusion of non-PKV kongor.net endpoints from active selection.
        // fails_when: IsKongorNetPkvEnabled defaults to true or kongor.net options are enabled by default.
        // seam: EndpointPairRegistry.IsKongorNetPkvEnabled and BuildAvailableCdnOptions.
        EndpointPairRegistry.IsKongorNetPkvEnabled.Should().BeFalse();

        var options = EndpointPairRegistry.BuildAvailableCdnOptions(null, null, false);

        var kongorNetCdn = options.FirstOrDefault(o => o.DisplayText == EndpointPairRegistry.CdnKongorNet);
        var kongorServicesCdn = options.FirstOrDefault(o => o.DisplayText == EndpointPairRegistry.CdnKongorNetServices);

        kongorNetCdn.Should().NotBeNull();
        kongorNetCdn!.IsEnabled.Should().BeFalse();

        kongorServicesCdn.Should().NotBeNull();
        kongorServicesCdn!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void EndpointPairRegistry_WhenKongorNetDisabled_ResolvePairedCdnOption_RedirectsToNewbsIo()
    {
        // protects: Auto-redirecting MasterServerKongorNet to CdnNewbsIo while kongor.net is not PKV v5 compliant.
        // fails_when: MasterServerKongorNet returns disabled CdnKongorNet while IsKongorNetPkvEnabled is false.
        // seam: EndpointPairRegistry.ResolvePairedCdnOption.
        EndpointPairRegistry.IsKongorNetPkvEnabled = false;

        var options = EndpointPairRegistry.BuildAvailableCdnOptions(null, null, false);
        var resolved = EndpointPairRegistry.ResolvePairedCdnOption(options, EndpointPairRegistry.MasterServerKongorNet, null, false);

        resolved.DisplayText.Should().Be(EndpointPairRegistry.CdnNewbsIo);
    }

    [Fact]
    public void EndpointPairRegistry_WhenKongorNetEnabled_ResolvePairedCdnOption_SelectsKongorNet()
    {
        // protects: Re-enabling kongor.net pairing cleanly once it transitions to PKV v5.
        // fails_when: IsKongorNetPkvEnabled = true fails to enable and pair kongor.net.
        // seam: EndpointPairRegistry.ResolvePairedCdnOption.
        try
        {
            EndpointPairRegistry.IsKongorNetPkvEnabled = true;

            var options = EndpointPairRegistry.BuildAvailableCdnOptions(null, null, false);
            var resolved = EndpointPairRegistry.ResolvePairedCdnOption(options, EndpointPairRegistry.MasterServerKongorNet, null, false);

            resolved.DisplayText.Should().Be(EndpointPairRegistry.CdnKongorNet);
            resolved.IsEnabled.Should().BeTrue();
        }
        finally
        {
            EndpointPairRegistry.IsKongorNetPkvEnabled = false;
        }
    }
}
