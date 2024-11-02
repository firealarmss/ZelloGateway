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
    public class ZelloToken
    {
        private const int TokenExpirationSeconds = 3000;

        public static string Base64UrlEncode(byte[] data)
        {
            var base64 = Convert.ToBase64String(data);
            return base64.Replace("+", "-").Replace("/", "_").Replace("=", "");
        }

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
