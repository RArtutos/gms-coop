using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// A rigged, animated body for a remote player, built from a glTF character file (GltfAvatar).
    /// Avatar.cs drives it like its own procedural body: same Animate inputs, plus attach points for the
    /// held tool and the name tag.
    /// </summary>
    internal interface IAvatarModel
    {
        /// <summary>Root object: Avatar parents it under its own root (feet at the local origin, facing +Z).</summary>
        GameObject Root { get; }

        /// <summary>Right hand attach point (tool visuals go here; local +Z points along the fingers).</summary>
        Transform RightHand { get; }

        /// <summary>Top of the head: the name tag goes a little above it.</summary>
        Transform NameAnchor { get; }

        /// <summary>
        /// Same inputs as Avatar.Animate: velocity in world space, PlayerSync.Flag* bits, the held
        /// PlayerTool.EPlayerToolType, camera pitch in degrees. Picks and cross-fades the clip
        /// (idle / walk / run / crouch / drive / use) and applies the carry pose on top.
        /// </summary>
        void Animate(bool driving, Vector3 velocity, byte flags, byte tool, float pitch);

        /// <summary>Tints the clothes with the player's color (skin and face keep their colors).</summary>
        void SetColor(Color c);

        void Destroy();
    }

    /// <summary>
    /// The characters a player can pick (launcher "Tu personaje", passed as --coop-character id).
    /// "game" is the game's own worker model and "procedural" the built-in block body (both in Avatar.cs);
    /// the others are glTF files shipped next to the plugin in GMSCoop_avatars\.
    /// </summary>
    internal static class AvatarModels
    {
        internal sealed class Character
        {
            public string Id;
            public string Name;
            public string File;
            /// <summary>Logical clip name -> clip name in the file (the files are pruned to logical names).</summary>
            public bool HasCarryClip;
            /// <summary>Bone names: right hand slot (or hand), head, upper/lower arms (for the carry pose).</summary>
            public string HandBone, HeadBone, UpperArmL, UpperArmR, LowerArmL, LowerArmR;
            /// <summary>Material names whose color is the "clothes" tinted with the player color.</summary>
            public string[] TintMaterials;
        }

        internal const string GameWorker = "worker";
        internal const string Procedural = "procedural";

        private static readonly Character[] All =
        {
            KayKit("kk_barbarian", "Bárbaro", "kaykit_barbarian.glb"),
            KayKit("kk_knight", "Caballero", "kaykit_knight.glb"),
            KayKit("kk_mage", "Mago", "kaykit_mage.glb"),
            KayKit("kk_rogue", "Pícaro", "kaykit_rogue.glb"),
            KayKit("kk_rogue_hooded", "Pícaro encapuchado", "kaykit_rogue_hooded.glb"),
            new Character
            {
                Id = "q_mannequin", Name = "Maniquí", File = "quaternius_mannequin.glb", HasCarryClip = true,
                HandBone = "DEF-hand.R", HeadBone = "DEF-head",
                UpperArmL = "DEF-upper_arm.L", UpperArmR = "DEF-upper_arm.R", LowerArmL = "DEF-forearm.L", LowerArmR = "DEF-forearm.R",
                TintMaterials = new[] { "M_Main" },
            },
        };

        private static Character KayKit(string id, string name, string file) => new Character
        {
            Id = id, Name = name, File = file, HasCarryClip = false,
            HandBone = "handslot.r", HeadBone = "head",
            UpperArmL = "upperarm.l", UpperArmR = "upperarm.r", LowerArmL = "lowerarm.l", LowerArmR = "lowerarm.r",
            TintMaterials = null, // one texture atlas: tint is a soft multiply on the whole body
        };

        /// <summary>The local player's choice (--coop-character), "game" when not given.</summary>
        internal static string LocalCharacter = GameWorker;

        internal static void ParseArgs(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--coop-character") LocalCharacter = Sanitize(args[i + 1]);
        }

        internal static string Sanitize(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 32) return GameWorker;
            foreach (char c in id) if (!(char.IsLetterOrDigit(c) || c == '_')) return GameWorker;
            return id;
        }

        internal static Character Find(string id)
        {
            foreach (var c in All) if (c.Id == id) return c;
            return null;
        }

        /// <summary>True for ids this PC can show as a glTF body (the file exists).</summary>
        internal static bool IsGltf(string id)
        {
            Character c = Find(id);
            return c != null && System.IO.File.Exists(Path.Combine(Folder, c.File));
        }

        internal static string Folder =>
            Path.Combine(Path.GetDirectoryName(typeof(AvatarModels).Assembly.Location) ?? ".", "GMSCoop_avatars");

        // ---------- name packet: "name<US>character" (old clients show the name with a short suffix) ----------

        private const char Sep = '\u001F';

        /// <summary>The name string PlayerSync sends: the player name plus the chosen character id.</summary>
        internal static string PackName(string name) =>
            LocalCharacter == GameWorker ? name : name + Sep + LocalCharacter;

        /// <summary>Splits a received name string into the display name and the character id ("game" if none).</summary>
        internal static string UnpackName(string packed, out string character)
        {
            character = GameWorker;
            if (packed == null) return null;
            int i = packed.IndexOf(Sep);
            if (i < 0) return packed;
            character = Sanitize(packed.Substring(i + 1));
            return packed.Substring(0, i);
        }

        // ---------- creation ----------

        /// <summary>
        /// A new body for character <paramref name="id"/>, or null while its file is still loading (call
        /// again later: loading starts on the first request and runs in the background), when the id is not a
        /// glTF character, or when its file failed to load.
        /// </summary>
        internal static IAvatarModel TryCreate(string id)
        {
            Character c = Find(id);
            if (c == null) return null;
            GltfTemplate t = GltfAvatarLoader.Get(c);
            return t != null && t.Ready ? new GltfAvatar(t) : null;
        }

        /// <summary>True when <paramref name="id"/> is a glTF character whose file failed: use a fallback for good.</summary>
        internal static bool Failed(string id)
        {
            Character c = Find(id);
            if (c == null) return true;
            GltfTemplate t = GltfAvatarLoader.Get(c);
            return t == null || t.Failed;
        }
    }
}
