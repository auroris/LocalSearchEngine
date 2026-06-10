using System;
using System.IO;
using SmartComponents.LocalEmbeddings;

Console.WriteLine("Initializing LocalEmbedder to trigger model download...");

// This instantiation will automatically download the default ONNX model if not cached.
using var embedder = new LocalEmbedder();

Console.WriteLine("Model is successfully downloaded and cached by SmartComponents.LocalEmbeddings.");

// Determine where the cache is (usually %LocalAppData%\SmartComponents\LocalEmbeddings)
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var cachePath = Path.Combine(localAppData, "SmartComponents", "LocalEmbeddings");

if (Directory.Exists(cachePath))
{
    var targetPath = Path.Combine(Directory.GetCurrentDirectory(), "OfflineModelCache");
    Console.WriteLine($"Copying cached model from {cachePath} to {targetPath}...");

    if (!Directory.Exists(targetPath))
    {
        Directory.CreateDirectory(targetPath);
    }

    foreach (var dirPath in Directory.GetDirectories(cachePath, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(dirPath.Replace(cachePath, targetPath));
    }

    foreach (var newPath in Directory.GetFiles(cachePath, "*.*", SearchOption.AllDirectories))
    {
        File.Copy(newPath, newPath.Replace(cachePath, targetPath), true);
    }

    Console.WriteLine();
    Console.WriteLine("==================================================");
    Console.WriteLine("SUCCESS!");
    Console.WriteLine($"The model has been bundled into: {targetPath}");
    Console.WriteLine("You can copy this folder to the offline machine and place it in the same %LocalAppData% path,");
    Console.WriteLine($@"i.e. {cachePath}");
    Console.WriteLine("==================================================");
}
else
{
    Console.WriteLine("Could not locate the SmartComponents cache folder.");
}
