using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    if (string.Equals(args[0], "generate", StringComparison.OrdinalIgnoreCase))
    {
        var privatePath = Require(args, "--private");
        var publicPath = Require(args, "--public");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privatePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicPath))!);
        if (File.Exists(privatePath) || File.Exists(publicPath))
            throw new IOException("Refusing to overwrite an existing signing key file.");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine(JsonSerializer.Serialize(new { generated = true, privateKey = Path.GetFullPath(privatePath), publicKey = Path.GetFullPath(publicPath) }));
        return 0;
    }

    if (string.Equals(args[0], "sign", StringComparison.OrdinalIgnoreCase))
    {
        var privatePath = Require(args, "--private");
        var version = Require(args, "--version");
        var packagePath = Require(args, "--package");
        var outputPath = Read(args, "--output");
        if (!Version.TryParse(version, out _)) throw new ArgumentException("Version must be a numeric System.Version value such as 1.2.0.");
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Update package was not found.", packagePath);

        var sha256 = await HashFileAsync(packagePath).ConfigureAwait(false);
        var signedData = Encoding.UTF8.GetBytes(version + "\n" + sha256 + "\n");
        using var key = ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(privatePath).ConfigureAwait(false));
        var signature = Convert.ToBase64String(key.SignData(signedData, HashAlgorithmName.SHA256));
        var metadata = new
        {
            schemaVersion = "1",
            version,
            package = Path.GetFullPath(packagePath),
            sha256,
            signatureBase64 = signature,
            algorithm = "ECDSA-P256-SHA256"
        };
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(outputPath)) await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(json);
        return 0;
    }

    PrintUsage();
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<string> HashFileAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
    while (true)
    {
        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
        if (read == 0) break;
        hash.AppendData(buffer, 0, read);
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

static string Require(string[] values, string name) => Read(values, name) ?? throw new ArgumentException($"Missing required argument: {name}");

static string? Read(string[] values, string name)
{
    for (var i = 0; i < values.Length - 1; i++)
        if (string.Equals(values[i], name, StringComparison.OrdinalIgnoreCase)) return values[i + 1];
    return null;
}

static void PrintUsage()
{
    Console.WriteLine("YazmaBackup.SigningTool generate --private <private.pem> --public <public.pem>");
    Console.WriteLine("YazmaBackup.SigningTool sign --private <private.pem> --version <x.y.z> --package <agent.zip> [--output metadata.json]");
}
