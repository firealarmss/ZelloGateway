// SPDX-License-Identifier: AGPL-3.0-only
/**
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024 Caleb, K4PHP
*
*/

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace ZelloGateway
{
    /// <summary>
    /// Zello Token
    /// </summary>
    public class ZelloToken
    {
        private const int TokenExpirationSeconds = 3000;

        /// <summary>
        /// Helper to base64 encode
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static string Base64UrlEncode(byte[] data)
        {
            var base64 = Convert.ToBase64String(data);
            return base64.Replace("+", "-").Replace("/", "_").Replace("=", "");
        }

        /// <summary>
        /// Create Zello JWT
        /// </summary>
        /// <param name="issuer"></param>
        /// <param name="pemPrivateKey"></param>
        /// <returns></returns>
        public static string CreateJwt(string issuer, string pemPrivateKey)
        {
            if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(pemPrivateKey))
            {
                return null;
            }

            var header = new
            {
                alg = "RS256",
                typ = "JWT"
            };

            var payload = new
            {
                iss = issuer,
                exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TokenExpirationSeconds
            };

            var headerEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(header)));
            var payloadEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload)));
            var tokenData = $"{headerEncoded}.{payloadEncoded}";

            byte[] signatureBytes;
            using (var rsa = GetDotNetRsaFromPem(pemPrivateKey))
            {
                signatureBytes = rsa.SignData(Encoding.UTF8.GetBytes(tokenData), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            var signatureEncoded = Base64UrlEncode(signatureBytes);
            return $"{tokenData}.{signatureEncoded}";
        }

        /// <summary>
        /// Get RSA from Pem file
        /// </summary>
        /// <param name="pemPrivateKey"></param>
        /// <returns></returns>
        /// <exception cref="InvalidCastException"></exception>
        public static RSA GetDotNetRsaFromPem(string pemPrivateKey)
        {
            using (var stringReader = new StringReader(pemPrivateKey))
            {
                var pemReader = new PemReader(stringReader);
                var keyObject = pemReader.ReadObject();

                if (keyObject is RsaPrivateCrtKeyParameters rsaPrivateKey)
                {
                    return DotNetUtilities.ToRSA(rsaPrivateKey);
                }
                else
                {
                    throw new InvalidCastException("The provided PEM string could not be parsed as an RSA private key.");
                }
            }
        }
    }
}
