using System.Windows.Media.Imaging;
using System.Text.Json;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed class SpeakerProfileStore
{
    private readonly object _gate = new();

    public IReadOnlyList<SpeakerProfile> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(AppPaths.SpeakerProfiles)) return Array.Empty<SpeakerProfile>();
            try
            {
                return JsonSerializer.Deserialize<List<SpeakerProfile>>(
                    File.ReadAllText(AppPaths.SpeakerProfiles),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<SpeakerProfile>();
            }
            catch
            {
                return Array.Empty<SpeakerProfile>();
            }
        }
    }

    private void SaveProfiles(List<SpeakerProfile> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SpeakerProfiles)!);
        var temp = AppPaths.SpeakerProfiles + ".tmp";
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temp, json);
        File.Move(temp, AppPaths.SpeakerProfiles, true);

        // Persistence proof: read the committed file back before reporting success.
        var verify = JsonSerializer.Deserialize<List<SpeakerProfile>>(
            File.ReadAllText(AppPaths.SpeakerProfiles),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (verify is null || verify.Count != list.Count)
            throw new IOException("Speaker profile persistence verification failed.");
    }

    public void Upsert(string name, float[] embedding, string? confirmedMeetingId = null, int? confirmedSpeakerIndex = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Speaker name is required.", nameof(name));
        if (embedding.Length == 0)
            throw new ArgumentException("Speaker embedding is empty.", nameof(embedding));

        lock (_gate)
        {
            var list = Load().ToList();
            var existing = list.FirstOrDefault(x =>
                x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var profile = new SpeakerProfile
                {
                    Name = name.Trim(),
                    Embedding = embedding,
                    CreatedLocal = DateTimeOffset.Now,
                    UpdatedLocal = DateTimeOffset.Now,
                    SampleCount = 1
                };

                if (!string.IsNullOrWhiteSpace(confirmedMeetingId))
                {
                    var meetingId = confirmedMeetingId.Trim();
                    profile.ConfirmedMeetingIds.Add(meetingId);

                    if (confirmedSpeakerIndex.HasValue)
                        profile.ConfirmedSpeakerRefs.Add(
                            $"{meetingId}:{confirmedSpeakerIndex.Value}");
                }

                list.Add(profile);
            }
            else
            {
                existing.Embedding = AverageNormalized(existing.Embedding, embedding);
                existing.UpdatedLocal = DateTimeOffset.Now;
                existing.SampleCount = Math.Max(1, existing.SampleCount) + 1;

                if (!string.IsNullOrWhiteSpace(confirmedMeetingId))
                {
                    var meetingId = confirmedMeetingId.Trim();

                    if (!existing.ConfirmedMeetingIds.Contains(
                            meetingId,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        existing.ConfirmedMeetingIds.Add(meetingId);
                    }

                    if (confirmedSpeakerIndex.HasValue)
                    {
                        var speakerRef = $"{meetingId}:{confirmedSpeakerIndex.Value}";
                        if (!existing.ConfirmedSpeakerRefs.Contains(
                                speakerRef,
                                StringComparer.OrdinalIgnoreCase))
                        {
                            existing.ConfirmedSpeakerRefs.Add(speakerRef);
                        }
                    }
                }
            }

            SaveProfiles(list);
        }
    }

    public string EnsureStableId(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Speaker profile name is required.", nameof(profileName));

        lock (_gate)
        {
            var list = Load().ToList();
            var existing = list.FirstOrDefault(x =>
                string.Equals(x.Name, profileName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                throw new InvalidOperationException("The speaker profile was not found.");
            if (string.IsNullOrWhiteSpace(existing.Id))
            {
                existing.Id = Guid.NewGuid().ToString("N");
                existing.UpdatedLocal = DateTimeOffset.Now;
                SaveProfiles(list);
            }
            return existing.Id;
        }
    }

    public void SetPhoto(string profileId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("Speaker profile ID is required.", nameof(profileId));
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Speaker photo was not found.", sourcePath);

        lock (_gate)
        {
            var list = Load().ToList();
            var existing = list.FirstOrDefault(x =>
                x.Id.Equals(profileId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                throw new InvalidOperationException("The speaker profile was not found.");

            Directory.CreateDirectory(AppPaths.SpeakerProfilePhotos);
            var version = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            var fileName = $"{AppPaths.Sanitize(existing.Name)}_{existing.Id}_{version}.png";
            var destination = Path.Combine(AppPaths.SpeakerProfilePhotos, fileName);
            NormalizePhotoToPng(sourcePath, destination);
            if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                throw new IOException("The person photo could not be persisted locally.");

            var previous = existing.PhotoPath;
            existing.PhotoPath = destination;
            existing.UpdatedLocal = DateTimeOffset.Now;
            SaveProfiles(list);

            var verify = Load().FirstOrDefault(x => x.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase));
            if (verify is null || !string.Equals(verify.PhotoPath, destination, StringComparison.OrdinalIgnoreCase) || !File.Exists(verify.PhotoPath))
                throw new IOException("The person photo did not survive profile reload verification.");

            if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, destination, StringComparison.OrdinalIgnoreCase))
            {
                try { if (File.Exists(previous)) File.Delete(previous); } catch { }
            }
        }
    }

    private static void NormalizePhotoToPng(string sourcePath, string destination)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("The image contains no readable frame.");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(decoder.Frames[0]);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    public void RegisterImportedVoiceSample(string profileId, string storedPath)
    {
        lock (_gate)
        {
            var list = Load().ToList();
            var existing = list.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (existing is null) throw new InvalidOperationException("The speaker profile was not found.");
            existing.ImportedVoiceSamplePaths ??= new List<string>();
            if (!existing.ImportedVoiceSamplePaths.Contains(storedPath, StringComparer.OrdinalIgnoreCase))
                existing.ImportedVoiceSamplePaths.Add(storedPath);
            existing.UpdatedLocal = DateTimeOffset.Now;
            SaveProfiles(list);
        }
    }

    public bool RemovePhoto(string name)
    {
        lock (_gate)
        {
            var list = Load().ToList();
            var existing = list.FirstOrDefault(x =>
                x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is null || string.IsNullOrWhiteSpace(existing.PhotoPath))
                return false;

            var previous = existing.PhotoPath;
            existing.PhotoPath = string.Empty;
            existing.UpdatedLocal = DateTimeOffset.Now;
            SaveProfiles(list);

            try
            {
                if (File.Exists(previous))
                    File.Delete(previous);
            }
            catch { }

            return true;
        }
    }

    public IReadOnlyList<SpeakerProfileMatch> FindTopMatches(float[] embedding, int limit = 3)
    {
        return Load()
            .Where(profile => profile.Embedding.Length == embedding.Length)
            .Select(profile => new SpeakerProfileMatch
            {
                ProfileId = profile.Id,
                Name = profile.Name,
                Score = Cosine(profile.Embedding, embedding)
            })
            .OrderByDescending(x => x.Score)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    public SpeakerProfileMatch? FindBestMatch(float[] embedding) =>
        FindTopMatches(embedding, 1).FirstOrDefault();

    public string FindBest(float[] embedding, float threshold)
    {
        var best = FindBestMatch(embedding);
        return best is not null && best.Score >= threshold
            ? best.Name
            : "";
    }

    public SpeakerProfile? FindByName(string name) =>
        Load().FirstOrDefault(x =>
            x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string ConfidenceLabel(float score, float threshold)
    {
        if (score >= Math.Min(0.99f, threshold + 0.12f))
            return $"Strong voice match • {score:P0} similarity";
        if (score >= threshold)
            return $"Voice match • {score:P0} similarity";
        if (score >= Math.Max(0.40f, threshold - 0.10f))
            return $"Possible • {score:P0} similarity";
        return $"Low similarity • {score:P0}";
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var list = Load().ToList();
            var removed = list.RemoveAll(x =>
                x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed) return false;
            SaveProfiles(list);
            return true;
        }
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }
        if (aa <= 0 || bb <= 0) return -1;
        return (float)(dot / (Math.Sqrt(aa) * Math.Sqrt(bb)));
    }

    private static float[] AverageNormalized(float[] a, float[] b)
    {
        if (a.Length != b.Length) return b.ToArray();
        var v = new float[a.Length];
        double norm = 0;
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (a[i] + b[i]) / 2f;
            norm += v[i] * v[i];
        }
        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (var i = 0; i < v.Length; i++)
                v[i] = (float)(v[i] / norm);
        return v;
    }
}
