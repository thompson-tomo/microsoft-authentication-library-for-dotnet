﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Internal;
using Microsoft.Identity.Client.Platforms.Features.KeyMaterial;

namespace Microsoft.Identity.Client.Credential
{
    /// <summary>
    /// Class to store crypto key information for a Managed Identity supported Azure resource.
    /// For more details see https://aka.ms/msal-net-managed-identity
    /// </summary>
    internal class TokenRequestAssertionInfo
    {
        private readonly X509Certificate2 _bindingCertificate;
        private readonly CertificateCache _certificateCache;
        private readonly object _keyInfoLock = new(); // Lock object
        private readonly ILoggerAdapter _logger;

        public TokenRequestAssertionInfo(IKeyMaterialManager keyMaterialManager,
            IServiceBundle serviceBundle)
        {
            _logger = serviceBundle.ApplicationLogger;

            _certificateCache = CertificateCache.Instance();

            _bindingCertificate = _certificateCache.GetOrAddCertificate(
                () => CreateCertificateFromCryptoKeyInfo(keyMaterialManager));
        }

        public X509Certificate2 BindingCertificate => _bindingCertificate;

        private X509Certificate2 CreateCertificateFromCryptoKeyInfo(IKeyMaterialManager keyMaterialManager)
        {
            lock (_keyInfoLock) // Lock to ensure thread safety
            {
                if (_bindingCertificate != null)
                {
                    _logger.Verbose(() => "[Managed Identity] A cached binding certificate is available.");
                    return _bindingCertificate;
                }
            }

            if (keyMaterialManager.CredentialKey != null)
            {
                return CreateCngCertificate(keyMaterialManager.CredentialKey);
            }

            return null;
        }

        private X509Certificate2 CreateCngCertificate(ECDsaCng eCDsaCngKey)
        {
            string certSubjectname = eCDsaCngKey.Key.KeyName;

            try
            {
                lock (_keyInfoLock) // Lock to ensure thread safety
                {
                    _logger.Verbose(() => "[Managed Identity] Creating binding certificate with CNG key for credential endpoint.");

                    // Create a certificate request
                    CertificateRequest request = CreateCertificateRequest(certSubjectname, eCDsaCngKey);

                    // Create a self-signed X.509 certificate
                    DateTimeOffset startDate = DateTimeOffset.UtcNow;
                    DateTimeOffset endDate = startDate.AddYears(2); //expiry 

                    //Create the self signed cert
                    X509Certificate2 selfSigned = request.CreateSelfSigned(startDate, endDate);

                    //create the cert with just the public key
                    X509Certificate2 publicKeyOnlyCertificate = new X509Certificate2(selfSigned.Export(X509ContentType.Cert));

                    //now copy the private key to the cert
                    //this is needed for mtls schannel to work with in-memory certificates
                    X509Certificate2 authCertificate = AssociatePrivateKeyInfo(publicKeyOnlyCertificate, eCDsaCngKey);

                    _logger.Verbose(() => "[Managed Identity] Binding certificate (with cng key) created successfully.");

                    return authCertificate;
                }
            }
            catch (CryptographicException ex)
            {
                // log the exception
                _logger.Error($"Error generating binding certificate: {ex.Message}");

                throw new MsalClientException(MsalError.CertificateCreationFailed,
                    $"Failed to create Managed Identity binding certificate. Error : {ex.Message}");
            }
        }

        private CertificateRequest CreateCertificateRequest(string subjectName, ECDsaCng ecdsaKey)
        {
            CertificateRequest certificateRequest = null;

            _logger.Verbose(() => "[Managed Identity] Creating certificate request for the binding certificate.");

            return certificateRequest = new(
                    $"CN={subjectName}", // Common Name 
                    ecdsaKey, // ECDsa key
                    HashAlgorithmName.SHA256); // Hash algorithm for the certificate
        }

        private X509Certificate2 AssociatePrivateKeyInfo(X509Certificate2 publicKeyOnlyCertificate, ECDsaCng eCDsaCngKey)
        {
            _logger.Verbose(() => "[Managed Identity] Associating private key with the binding certificate.");
            return publicKeyOnlyCertificate.CopyWithPrivateKey(eCDsaCngKey);
        }
    }
}