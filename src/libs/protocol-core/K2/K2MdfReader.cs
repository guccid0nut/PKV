// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Xml.Linq;

namespace ASPIRE.Common.K2;

public static class K2MdfReader
{
    public static K2Mdf Read(Stream stream)
    {
        K2Mdf mdf = new();
        XDocument doc = XDocument.Load(stream);
        XElement? root = doc.Root;
        if (root is null) return mdf;

        mdf.ModelFile = root.Attribute("high")?.Value ??
                        root.Attribute("file")?.Value ??
                        root.Attribute("med")?.Value ??
                        string.Empty;

        // Parse attachments on root model
        foreach (XElement attachEl in root.Elements("attachment"))
        {
            string? bone = attachEl.Attribute("bone")?.Value;
            string? modelPath = attachEl.Attribute("model")?.Value ?? attachEl.Attribute("file")?.Value;
            if (!string.IsNullOrEmpty(bone) && !string.IsNullOrEmpty(modelPath))
                mdf.Attachments.Add(new K2MdfAttachment { Bone = bone, ModelPath = modelPath });
        }

        foreach (XElement animElement in root.Elements("anim"))
        {
            string? name = animElement.Attribute("name")?.Value;
            string? clip = animElement.Attribute("clip")?.Value;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(clip)) continue;

            int fps = 30;
            if (int.TryParse(animElement.Attribute("fps")?.Value, out int parsedFps) && parsedFps > 0) fps = parsedFps;

            bool loop = true;
            if (bool.TryParse(animElement.Attribute("loop")?.Value, out bool parsedLoop)) loop = parsedLoop;

            K2MdfAnim anim = new() { Name = name, ClipPath = clip, Fps = fps, Loop = loop };

            foreach (XElement frameEventEl in animElement.Elements("frameevent"))
            {
                int frame = int.TryParse(frameEventEl.Attribute("frame")?.Value, out int parsedFrame) ? parsedFrame : 0;
                string cmd = frameEventEl.Attribute("cmd")?.Value ?? string.Empty;

                string? soundPath = null;
                string? effectPath = null;

                if (cmd.StartsWith("PlaySound", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1) soundPath = parts[1].Replace("%", "1");
                }
                else if (cmd.StartsWith("StartEffect", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1) effectPath = parts[1];
                }

                anim.FrameEvents.Add(new K2MdfFrameEvent
                {
                    Frame = frame,
                    Command = cmd,
                    SoundPath = soundPath,
                    EffectPath = effectPath
                });
            }

            mdf.Animations.Add(anim);
        }

        return mdf;
    }
}
