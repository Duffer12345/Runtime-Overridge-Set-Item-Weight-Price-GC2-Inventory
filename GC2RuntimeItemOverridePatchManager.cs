#if UNITY_EDITOR

/*
    GC2RuntimeItemOverridePatchManager.cs
    ------------------------------------------------------------------------------

    PURPOSE
    -------
    This Unity Editor tool non-destructively patches Game Creator 2 Inventory's
    RuntimeItem.cs file so that existing GC2 systems which read:

        runtimeItem.Price
        runtimeItem.Weight

    automatically honour per-runtime-item price and weight overrides.

    WHY PATCH RUNTIMEITEM?
    ----------------------
    Game Creator 2 Inventory UI, Bags, merchants and weight calculations generally
    read RuntimeItem.Price and RuntimeItem.Weight directly. Therefore, if those two
    getters remain unchanged, any custom external override system will not be seen
    by the existing GC2 UI without replacing or re-hacking the UI.

    This tool therefore patches those getter paths directly, but does so safely:

        1. It creates a timestamped backup before editing.
        2. It uses clear patch markers.
        3. It can verify whether the patch is installed.
        4. It can remove the patch markers and restore the original getter code.
        5. It can restore from the most recent backup.

    IMPORTANT
    ---------
    This script must live inside an Editor folder.

        Assets/Editor/GC2RuntimeItemOverridePatchManager.cs

    After applying or removing the patch, Unity will recompile scripts.
*/

using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public class GC2RuntimeItemOverridePatchManager : EditorWindow
{
    // -------------------------------------------------------------------------
    // PATCH MARKERS
    // -------------------------------------------------------------------------
    // These unique strings are deliberately verbose and unlikely to appear
    // naturally in GC2 source code. They allow the tool to find, verify and
    // remove exactly the code it added without guessing.

    private const string PATCH_ID = "NIALL_GC2_RUNTIME_ITEM_PRICE_WEIGHT_OVERRIDE_PATCH";

    private const string FIELDS_START =
        "// >>> " + PATCH_ID + "_FIELDS_START";

    private const string FIELDS_END =
        "// <<< " + PATCH_ID + "_FIELDS_END";

    private const string METHODS_START =
        "// >>> " + PATCH_ID + "_METHODS_START";

    private const string METHODS_END =
        "// <<< " + PATCH_ID + "_METHODS_END";

    private const string WEIGHT_START =
        "// >>> " + PATCH_ID + "_WEIGHT_PROPERTY_START";

    private const string WEIGHT_END =
        "// <<< " + PATCH_ID + "_WEIGHT_PROPERTY_END";

    private const string PRICE_START =
        "// >>> " + PATCH_ID + "_PRICE_PROPERTY_START";

    private const string PRICE_END =
        "// <<< " + PATCH_ID + "_PRICE_PROPERTY_END";

    // These names identify the four generated GC2 integration scripts.
    // Keeping them as constants avoids typo-risk when generating, disabling,
    // deleting, or searching for duplicate copies.
    private const string SCRIPT_SET_PRICE = "InstructionRuntimeItemSetPriceOverride.cs";
    private const string SCRIPT_SET_WEIGHT = "InstructionRuntimeItemSetWeightOverride.cs";
    private const string SCRIPT_GET_PRICE = "GetDecimalRuntimeItemPrice.cs";
    private const string SCRIPT_GET_WEIGHT = "GetDecimalRuntimeItemWeight.cs";
    private const string SCRIPT_HELPER = "RuntimeItemOverrideUtility.cs";

    // -------------------------------------------------------------------------
    // USER-FACING SETTINGS
    // -------------------------------------------------------------------------

    [SerializeField]
    [Tooltip("Path to Game Creator 2's RuntimeItem.cs file. Use Auto-Find if unsure.")]
    private string runtimeItemPath = "";

    [SerializeField]
    [Tooltip("Folder where timestamped backups of RuntimeItem.cs are stored before any patch is applied.")]
    private string backupRootFolder = "Assets/_Backups/GC2 Runtime Item Override Patches";

    [SerializeField]
    [Tooltip("If enabled, applying the patch also creates a small runtime helper script with convenience extension methods.")]
    private bool generateHelperScript = true;

    [SerializeField]
    [Tooltip("Folder where optional generated helper scripts are written.")]
    private string generatedScriptsFolder = "Assets/GameCreatorExtensions/InventoryRuntimeOverrides";

    private Vector2 scroll;

    private enum GeneratedScriptRemovalMode
    {
        RenameToDisabled,
        DeletePermanently
    }

    [SerializeField]
    [Tooltip("If enabled, applying the RuntimeItem patch also creates the GC2 Actions and Property Getters that use the patched runtime override API.")]
    private bool generateGC2IntegrationScripts = true;

    [SerializeField]
    [Tooltip("Folder where the generated GC2 Set Action scripts are written.")]
    private string gc2ActionScriptsFolder =
    "Assets/Scripts/GameCreator Specific/Actions";

    [SerializeField]
    [Tooltip("Folder where the generated GC2 Get Decimal property override scripts are written.")]
    private string gc2PropertyGetterScriptsFolder =
        "Assets/Scripts/GameCreator Specific/Properties";

    [SerializeField]
    [Tooltip("Controls what happens to the generated GC2 scripts when the RuntimeItem patch is removed. Rename is safer; Delete is cleaner.")]
    private GeneratedScriptRemovalMode generatedScriptRemovalMode =
        GeneratedScriptRemovalMode.RenameToDisabled;

    [SerializeField]
    [Tooltip("If enabled, removal scans the whole Assets folder for all copies of the four generated GC2 scripts, not just the configured folders.")]
    private bool removeGeneratedScriptsEverywhere = true;

    // -------------------------------------------------------------------------
    // MENU
    // -------------------------------------------------------------------------

    [MenuItem("Tools/Niall Tools/Game Creator 2/Runtime Item Price & Weight Patch Manager")]
    public static void Open()
    {
        GetWindow<GC2RuntimeItemOverridePatchManager>(
            "GC2 Runtime Item Patch"
        );
    }

    // -------------------------------------------------------------------------
    // GUI
    // -------------------------------------------------------------------------

    private void OnGUI()
    {
        this.scroll = EditorGUILayout.BeginScrollView(this.scroll);

        DrawHeader();

        EditorGUILayout.Space(8);
        DrawConfiguration();

        EditorGUILayout.Space(8);
        DrawStatus();

        EditorGUILayout.Space(8);
        DrawActions();

        EditorGUILayout.Space(8);
        DrawExplanation();

        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        EditorGUILayout.LabelField(
            "GC2 Runtime Item Price & Weight Override Patch Manager",
            EditorStyles.boldLabel
        );

        EditorGUILayout.HelpBox(
            "This tool patches RuntimeItem.cs so existing Game Creator 2 Inventory systems " +
            "and UI automatically honour per-runtime-item price and weight overrides. " +
            "It creates backups first and can reverse the patch.",
            MessageType.Info
        );
    }

    private void DrawConfiguration()
    {
        
        EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);

        runtimeItemPath = EditorGUILayout.TextField(
            new GUIContent(
                "RuntimeItem.cs Path",
                "The path to Game Creator 2's RuntimeItem.cs file. This is the file that contains RuntimeItem.Price and RuntimeItem.Weight."
            ),
            runtimeItemPath
        );

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent(
                    "Auto-Find RuntimeItem.cs",
                    "Searches the project for a C# script named RuntimeItem.cs that contains the GameCreator.Runtime.Inventory RuntimeItem class."
                )))
            {
                AutoFindRuntimeItem();
            }

            if (GUILayout.Button(new GUIContent(
                    "Ping File",
                    "Highlights the selected RuntimeItem.cs file in the Project window."
                )))
            {
                PingRuntimeItemFile();
            }
        }

        backupRootFolder = EditorGUILayout.TextField(
            new GUIContent(
                "Backup Root Folder",
                "Before patching, the tool copies RuntimeItem.cs into a timestamped subfolder here."
            ),
            backupRootFolder
        );

        generateHelperScript = EditorGUILayout.Toggle(
            new GUIContent(
                "Generate Helper Script",
                "Creates a small helper script with convenience extension methods. The core patch does not depend on it."
            ),
            generateHelperScript
        );

        using (new EditorGUI.DisabledScope(!generateHelperScript))
        {
            generatedScriptsFolder = EditorGUILayout.TextField(
                new GUIContent(
                    "Generated Scripts Folder",
                    "Where optional helper scripts are created."
                ),
                generatedScriptsFolder
            );
        }

        EditorGUILayout.Space(6);

        generateGC2IntegrationScripts = EditorGUILayout.Toggle(
            new GUIContent(
                "Generate GC2 Integration Scripts",
                "When enabled, applying the patch also creates the two Set Actions and two Get Decimal property scripts."
            ),
            generateGC2IntegrationScripts
        );

        using (new EditorGUI.DisabledScope(!generateGC2IntegrationScripts))
        {
            gc2ActionScriptsFolder = EditorGUILayout.TextField(
                new GUIContent(
                    "GC2 Action Scripts Folder",
                    "The folder where the two Set override Action scripts are created: Set Runtime Item Price Override and Set Runtime Item Weight Override."
                ),
                gc2ActionScriptsFolder
            );

            gc2PropertyGetterScriptsFolder = EditorGUILayout.TextField(
                new GUIContent(
                    "GC2 Property Getter Scripts Folder",
                    "The folder where the two Get Decimal override scripts are created: Runtime Item Price and Runtime Item Weight."
                ),
                gc2PropertyGetterScriptsFolder
            );
        }

        removeGeneratedScriptsEverywhere = EditorGUILayout.Toggle(
        new GUIContent(
                "Remove Generated Scripts Everywhere",
                "When enabled, the tool searches the whole Assets folder for all copies of the four generated scripts and disables/deletes them, even if they are not in the configured folders."
            ),
            removeGeneratedScriptsEverywhere
        );
        generatedScriptRemovalMode = (GeneratedScriptRemovalMode)EditorGUILayout.EnumPopup(
        new GUIContent(
                "Generated Script Removal Mode",
                "When removing the patch, either rename generated scripts to .disabled or delete them permanently. Rename is recommended."
        ),
        generatedScriptRemovalMode
        );
    }

    private void DrawStatus()
    {
        EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);

        if (string.IsNullOrWhiteSpace(runtimeItemPath))
        {
            EditorGUILayout.HelpBox(
                "RuntimeItem.cs has not yet been selected. Use Auto-Find or paste the path manually.",
                MessageType.Warning
            );
            return;
        }

        if (!File.Exists(runtimeItemPath))
        {
            EditorGUILayout.HelpBox(
                "The selected RuntimeItem.cs path does not exist.",
                MessageType.Error
            );
            return;
        }

        string text = File.ReadAllText(runtimeItemPath);

        bool hasFields = text.Contains(FIELDS_START) && text.Contains(FIELDS_END);
        bool hasMethods = text.Contains(METHODS_START) && text.Contains(METHODS_END);
        bool hasWeight = text.Contains(WEIGHT_START) && text.Contains(WEIGHT_END);
        bool hasPrice = text.Contains(PRICE_START) && text.Contains(PRICE_END);

        if (hasFields && hasMethods && hasWeight && hasPrice)
        {
            EditorGUILayout.HelpBox(
                "Patch appears to be fully installed.",
                MessageType.Info
            );
        }
        else if (hasFields || hasMethods || hasWeight || hasPrice)
        {
            EditorGUILayout.HelpBox(
                "Partial patch markers detected. This usually means the file was manually edited or a previous patch operation was interrupted. Restore from backup or remove the patch before applying again.",
                MessageType.Warning
            );
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Patch is not currently installed.",
                MessageType.None
            );
        }
    }

    private void DrawActions()
    {
        EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent(
                    "Apply Patch",
                    "Creates a backup, then patches RuntimeItem.cs so Price and Weight honour runtime overrides."
                ), GUILayout.Height(34)))
            {
                ApplyPatchWithDialog();
            }

            if (GUILayout.Button(new GUIContent(
                    "Remove Patch",
                    "Removes only the marked patch blocks and restores the original RuntimeItem.cs Price and Weight getters."
                ), GUILayout.Height(34)))
            {
                RemovePatchWithDialog();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent(
                    "Restore Latest Backup",
                    "Restores RuntimeItem.cs from the most recent backup created by this tool."
                )))
            {
                RestoreLatestBackupWithDialog();
            }

            if (GUILayout.Button(new GUIContent(
                    "Generate Helper Script Only",
                    "Creates only the optional helper extension script without modifying RuntimeItem.cs."
                )))
            {
                GenerateHelperScript();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent(
                    "Generate GC2 Scripts Only",
                    "Creates the four GC2 integration scripts without modifying RuntimeItem.cs. Only use after RuntimeItem.cs is patched."
                )))
            {
                GenerateGC2IntegrationScripts();
            }

            if (GUILayout.Button(new GUIContent(
                    "Disable/Delete GC2 Scripts",
                    "Disables or deletes the generated GC2 integration scripts, depending on the selected removal mode."
                )))
            {
                RemoveGeneratedGC2IntegrationScripts();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent(
                    "Run Compatibility Check",
                    "Checks whether the selected RuntimeItem.cs looks compatible with this patcher before applying or removing the patch."
                )))
            {
                RunCompatibilityCheckWithDialog();
            }
        }
    }

    private void DrawExplanation()
    {
        EditorGUILayout.LabelField("What This Patch Adds", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "The patch adds serialized override fields to RuntimeItem:\n\n" +
            "• HasRuntimePriceOverride\n" +
            "• RuntimePriceOverride\n" +
            "• HasRuntimeWeightOverride\n" +
            "• RuntimeWeightOverride\n\n" +
            "It also adds public methods:\n\n" +
            "• SetRuntimePriceOverride(int)\n" +
            "• ClearRuntimePriceOverride()\n" +
            "• TryGetRuntimePriceOverride(out int)\n" +
            "• SetRuntimeWeightOverride(int)\n" +
            "• ClearRuntimeWeightOverride()\n" +
            "• TryGetRuntimeWeightOverride(out int)\n\n" +
            "The existing RuntimeItem.Price and RuntimeItem.Weight getters are then patched to check these overrides first.",
            MessageType.None
        );
    }

    // -------------------------------------------------------------------------
    // CORE PATCH OPERATIONS
    // -------------------------------------------------------------------------

    private void ApplyPatchWithDialog()
    {
        if (!ValidateRuntimeItemPath()) return;

        if (!EditorUtility.DisplayDialog(
                "Apply RuntimeItem Patch?",
                "This will create a timestamped backup of RuntimeItem.cs and then patch the file.\n\n" +
                "Unity will recompile scripts afterwards.",
                "Apply Patch",
                "Cancel"))
        {
            return;
        }

        try
        {
            CreateBackup();
            ApplyPatch();

            if (generateHelperScript)
            {
                GenerateHelperScript();
            }

            if (generateGC2IntegrationScripts)
            {
                GenerateGC2IntegrationScripts();
            }

            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Patch Applied",
                "RuntimeItem.cs was patched successfully.",
                "OK"
            );
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog(
                "Patch Failed",
                ex.Message,
                "OK"
            );
        }
    }

    private void RemovePatchWithDialog()
    {
        if (!ValidateRuntimeItemPath()) return;

        if (!EditorUtility.DisplayDialog(
                "Remove RuntimeItem Patch?",
                "This will disable/delete generated GC2 scripts, create a backup, and then remove the marked patch blocks from RuntimeItem.cs.\n\n" +
                "If marker-based removal fails, the tool can try to restore the latest vanilla RuntimeItem backup.",
                "Remove Patch",
                "Cancel"))
        {
            return;
        }

        try
        {
            RemoveGeneratedGC2IntegrationScripts();

            CreateBackup();

            try
            {
                RemovePatch();
            }
            catch (Exception markerRemovalException)
            {
                Debug.LogWarning(
                    "Marker-based patch removal failed. Attempting vanilla backup fallback.\n\n" +
                    markerRemovalException
                );

                string vanillaBackup = FindLatestVanillaRuntimeItemBackup();

                if (string.IsNullOrEmpty(vanillaBackup))
                {
                    throw;
                }

                if (!EditorUtility.DisplayDialog(
                        "Marker Removal Failed",
                        $"The tool could not safely remove the patch markers, but found a vanilla backup:\n\n{vanillaBackup}\n\nRestore this instead?",
                        "Restore Vanilla Backup",
                        "Cancel"))
                {
                    throw;
                }

                File.Copy(vanillaBackup, runtimeItemPath, true);
            }

            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Patch Removed",
                "The RuntimeItem patch was removed or RuntimeItem.cs was restored from a vanilla backup.",
                "OK"
            );
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog(
                "Removal Failed",
                ex.Message,
                "OK"
            );
        }
    }

    private void RunCompatibilityCheckWithDialog()
    {
        if (!ValidateRuntimeItemPath()) return;

        string text = File.ReadAllText(runtimeItemPath);

        bool hasNamespace = text.Contains("namespace GameCreator.Runtime.Inventory");
        bool hasRuntimeItemClass = text.Contains("public class RuntimeItem");
        bool hasRuntimeProperties = text.Contains("RuntimeProperties");
        bool hasRuntimeSockets = text.Contains("RuntimeSockets");
        bool hasWeightProperty = text.Contains("public int Weight");
        bool hasPriceProperty = text.Contains("public int Price");
        bool hasFieldsPatch = text.Contains(FIELDS_START) && text.Contains(FIELDS_END);
        bool hasMethodsPatch = text.Contains(METHODS_START) && text.Contains(METHODS_END);
        bool hasWeightPatch = text.Contains(WEIGHT_START) && text.Contains(WEIGHT_END);
        bool hasPricePatch = text.Contains(PRICE_START) && text.Contains(PRICE_END);

        string report =
            "RuntimeItem Compatibility Check\n\n" +
            $"File: {runtimeItemPath}\n\n" +
            $"Namespace found: {(hasNamespace ? "YES" : "NO")}\n" +
            $"RuntimeItem class found: {(hasRuntimeItemClass ? "YES" : "NO")}\n" +
            $"RuntimeProperties reference found: {(hasRuntimeProperties ? "YES" : "NO")}\n" +
            $"RuntimeSockets reference found: {(hasRuntimeSockets ? "YES" : "NO")}\n" +
            $"Weight property signature found: {(hasWeightProperty ? "YES" : "NO")}\n" +
            $"Price property signature found: {(hasPriceProperty ? "YES" : "NO")}\n\n" +
            "Patch Marker Status\n" +
            $"Fields patch: {(hasFieldsPatch ? "YES" : "NO")}\n" +
            $"Methods patch: {(hasMethodsPatch ? "YES" : "NO")}\n" +
            $"Weight patch: {(hasWeightPatch ? "YES" : "NO")}\n" +
            $"Price patch: {(hasPricePatch ? "YES" : "NO")}\n\n" +
            "Interpretation\n" +
            "If the namespace, class, RuntimeProperties, RuntimeSockets, Weight and Price checks are YES, this file is likely compatible.\n" +
            "If the patch marker checks are all YES, it is already patched.";

        EditorUtility.DisplayDialog(
            "Compatibility Check",
            report,
            "OK"
        );

        Debug.Log(report);
    }

    private void ApplyPatch()
    {
        string text = File.ReadAllText(runtimeItemPath);

        if (text.Contains(PATCH_ID))
        {
            throw new InvalidOperationException(
                "Patch markers are already present. Remove the existing patch or restore from backup before applying again."
            );
        }

        text = InsertFields(text);
        text = ReplaceWeightProperty(text);
        text = ReplacePriceProperty(text);
        text = InsertMethods(text);

        File.WriteAllText(runtimeItemPath, text, Encoding.UTF8);
    }

    private void RemovePatch()
    {
        string text = File.ReadAllText(runtimeItemPath);

        text = RemoveMarkedBlock(text, FIELDS_START, FIELDS_END);
        text = RemoveMarkedBlock(text, METHODS_START, METHODS_END);
        text = ReplaceMarkedBlock(text, WEIGHT_START, WEIGHT_END, OriginalWeightProperty);
        text = ReplaceMarkedBlock(text, PRICE_START, PRICE_END, OriginalPriceProperty);

        File.WriteAllText(runtimeItemPath, text, Encoding.UTF8);
    }

    private void GenerateGC2IntegrationScripts()
    {
        Directory.CreateDirectory(gc2ActionScriptsFolder);
        Directory.CreateDirectory(gc2PropertyGetterScriptsFolder);

        WriteGeneratedScript(
            gc2ActionScriptsFolder,
            SCRIPT_SET_PRICE,
            InstructionSetPriceSource
        );

        WriteGeneratedScript(
            gc2ActionScriptsFolder,
            SCRIPT_SET_WEIGHT,
            InstructionSetWeightSource
        );

        WriteGeneratedScript(
            gc2PropertyGetterScriptsFolder,
            SCRIPT_GET_PRICE,
            GetPriceSource
        );

        WriteGeneratedScript(
            gc2PropertyGetterScriptsFolder,
            SCRIPT_GET_WEIGHT,
            GetWeightSource
        );

        AssetDatabase.Refresh();

        Debug.Log("Generated GC2 Runtime Item Price/Weight integration scripts.");
    }

    private void WriteGeneratedScript(string folder, string fileName, string source)
    {
        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, fileName);
        string disabledPath = path + ".disabled";

        if (File.Exists(disabledPath) && !File.Exists(path))
        {
            File.Move(disabledPath, path);
        }

        File.WriteAllText(path, source, Encoding.UTF8);
    }

    private void RemoveGeneratedGC2IntegrationScripts()
    {
        /*
            This method removes/disables every script generated by this tool.

            Important:
            ----------
            Earlier versions only removed the four GC2 integration scripts:

                - InstructionRuntimeItemSetPriceOverride.cs
                - InstructionRuntimeItemSetWeightOverride.cs
                - GetDecimalRuntimeItemPrice.cs
                - GetDecimalRuntimeItemWeight.cs

            However, this tool can also generate the optional helper script:

                - RuntimeItemOverrideUtility.cs

            That helper script references the patched RuntimeItem API. Therefore, once
            RuntimeItem.cs is unpatched, leaving RuntimeItemOverrideUtility.cs active can
            cause compile errors because methods such as SetRuntimePriceOverride no longer
            exist on RuntimeItem.

            So full patch removal should also disable or delete RuntimeItemOverrideUtility.cs.
        */

        if (removeGeneratedScriptsEverywhere)
        {
            RemoveGeneratedScriptEverywhere(SCRIPT_SET_PRICE);
            RemoveGeneratedScriptEverywhere(SCRIPT_SET_WEIGHT);
            RemoveGeneratedScriptEverywhere(SCRIPT_GET_PRICE);
            RemoveGeneratedScriptEverywhere(SCRIPT_GET_WEIGHT);

            // Also remove/disable the optional helper script wherever it is found.
            RemoveGeneratedScriptEverywhere(SCRIPT_HELPER);
        }
        else
        {
            RemoveGeneratedScript(gc2ActionScriptsFolder, SCRIPT_SET_PRICE);
            RemoveGeneratedScript(gc2ActionScriptsFolder, SCRIPT_SET_WEIGHT);

            RemoveGeneratedScript(gc2PropertyGetterScriptsFolder, SCRIPT_GET_PRICE);
            RemoveGeneratedScript(gc2PropertyGetterScriptsFolder, SCRIPT_GET_WEIGHT);

            // Also remove/disable the helper script from the configured helper folder.
            RemoveGeneratedScript(generatedScriptsFolder, SCRIPT_HELPER);
        }

        AssetDatabase.Refresh();
    }

    private void RemoveGeneratedScriptEverywhere(string fileName)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;

        string[] activeMatches = Directory.GetFiles(
            Application.dataPath,
            fileName,
            SearchOption.AllDirectories
        );

        string[] disabledMatches = Directory.GetFiles(
            Application.dataPath,
            fileName + ".disabled",
            SearchOption.AllDirectories
        );

        string[] allMatches = activeMatches
            .Concat(disabledMatches)
            .Distinct()
            .ToArray();

        foreach (string absolutePath in allMatches)
        {
            string relativePath = absolutePath.Replace(
                projectRoot + Path.DirectorySeparatorChar,
                ""
            );

            relativePath = relativePath.Replace("\\", "/");

            if (relativePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                HandleAlreadyDisabledGeneratedScript(relativePath);
            }
            else
            {
                RemoveGeneratedScript(
                    Path.GetDirectoryName(relativePath),
                    Path.GetFileName(relativePath)
                );
            }
        }
    }

    private void HandleAlreadyDisabledGeneratedScript(string relativeDisabledPath)
    {
        if (!File.Exists(relativeDisabledPath)) return;

        if (generatedScriptRemovalMode == GeneratedScriptRemovalMode.DeletePermanently)
        {
            File.Delete(relativeDisabledPath);

            string disabledMeta = relativeDisabledPath + ".meta";
            if (File.Exists(disabledMeta)) File.Delete(disabledMeta);

            Debug.Log($"Deleted disabled generated GC2 script: {relativeDisabledPath}");
            return;
        }

        Debug.Log($"Generated GC2 script is already disabled: {relativeDisabledPath}");
    }

    private void RemoveGeneratedScript(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return;

        if (generatedScriptRemovalMode == GeneratedScriptRemovalMode.DeletePermanently)
        {
            File.Delete(path);

            string meta = path + ".meta";
            if (File.Exists(meta)) File.Delete(meta);

            Debug.Log($"Deleted generated GC2 script: {path}");
            return;
        }

        string disabledPath = path + ".disabled";

        if (File.Exists(disabledPath))
        {
            File.Delete(disabledPath);
        }

        File.Move(path, disabledPath);

        string metaPath = path + ".meta";
        if (File.Exists(metaPath))
        {
            File.Move(metaPath, disabledPath + ".meta");
        }

        Debug.Log($"Disabled generated GC2 script: {path}");
    }

    // -------------------------------------------------------------------------
    // PATCH INSERTION
    // -------------------------------------------------------------------------

    private string InsertFields(string text)
    {
        /*
            Purpose:
            --------
            Adds the serialized override fields to RuntimeItem.cs.

            Future-proofing:
            ----------------
            Older versions of this tool looked only for the exact RuntimeSockets field.
            That works for the uploaded GC2 version, but a later Inventory 2 version may
            move fields around or alter comments.

            This version therefore tries several safe anchors in order.
        */

        if (text.Contains(FIELDS_START))
        {
            return text;
        }

        string[] preferredAnchors =
        {
        "[SerializeField] private RuntimeSockets m_Sockets;",
        "[SerializeField] private RuntimeProperties m_Properties;",
        "// MEMBERS: -------------------------------------------------------------------------------"
    };

        foreach (string anchor in preferredAnchors)
        {
            int index = text.IndexOf(anchor, StringComparison.Ordinal);
            if (index < 0) continue;

            int insertIndex = index + anchor.Length;

            // If the anchor is the MEMBERS comment, insert before it rather than after it.
            if (anchor.StartsWith("// MEMBERS", StringComparison.Ordinal))
            {
                insertIndex = index;
            }

            return text.Insert(insertIndex, "\n\n" + PatchedFields + "\n");
        }

        throw new InvalidOperationException(
            "Could not find a safe place to insert RuntimeItem override fields. " +
            "Expected RuntimeSockets, RuntimeProperties, or the MEMBERS section."
        );
    }

    private string InsertMethods(string text)
    {
        /*
            Purpose:
            --------
            Adds the public Set/Clear/TryGet runtime override API to RuntimeItem.cs.

            Future-proofing:
            ----------------
            The preferred insertion point is still the CONSTRUCTORS heading because it
            keeps the file readable. If a future GC2 version removes or renames that
            comment, we fall back to inserting before the first RuntimeItem constructor.
        */

        if (text.Contains(METHODS_START))
        {
            return text;
        }

        string constructorsComment = "// CONSTRUCTORS: --------------------------------------------------------------------------";
        int commentIndex = text.IndexOf(constructorsComment, StringComparison.Ordinal);

        if (commentIndex >= 0)
        {
            return text.Insert(commentIndex, PatchedMethods + "\n\n        ");
        }

        string[] constructorAnchors =
        {
        "public RuntimeItem()",
        "public RuntimeItem(Item item)",
        "public RuntimeItem(RuntimeItem runtimeItem"
    };

        foreach (string anchor in constructorAnchors)
        {
            int index = text.IndexOf(anchor, StringComparison.Ordinal);
            if (index < 0) continue;

            return text.Insert(index, PatchedMethods + "\n\n        ");
        }

        throw new InvalidOperationException(
            "Could not find a safe place to insert RuntimeItem override methods. " +
            "Expected the CONSTRUCTORS section or a RuntimeItem constructor."
        );
    }

    private string ReplaceWeightProperty(string text)
    {
        return ReplacePropertyByName(
            text,
            "public int Weight",
            PatchedWeightProperty,
            "RuntimeItem.Weight"
        );
    }

    private string ReplacePriceProperty(string text)
    {
        return ReplacePropertyByName(
            text,
            "public int Price",
            PatchedPriceProperty,
            "RuntimeItem.Price"
        );
    }

    private static string ReplacePropertyByName(
        string text,
        string propertySignature,
        string replacement,
        string friendlyName
    )
    {
        int propertyStart = text.IndexOf(propertySignature, StringComparison.Ordinal);

        if (propertyStart < 0)
        {
            throw new InvalidOperationException(
                $"Could not find {friendlyName}. Looked for signature: {propertySignature}"
            );
        }

        int semicolon = text.IndexOf(';', propertyStart);
        int braceStart = text.IndexOf('{', propertyStart);

        if (semicolon >= 0 && (braceStart < 0 || semicolon < braceStart))
        {
            return text.Substring(0, propertyStart) +
                   replacement +
                   text.Substring(semicolon + 1);
        }

        if (braceStart < 0)
        {
            throw new InvalidOperationException(
                $"Found {friendlyName}, but could not find its opening brace."
            );
        }

        int depth = 0;

        for (int i = braceStart; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;

            if (depth == 0)
            {
                return text.Substring(0, propertyStart) +
                       replacement +
                       text.Substring(i + 1);
            }
        }

        throw new InvalidOperationException(
            $"Found {friendlyName}, but could not find its closing brace."
        );
    }



    // -------------------------------------------------------------------------
    // PATCH REMOVAL HELPERS
    // -------------------------------------------------------------------------

    private static string RemoveMarkedBlock(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0) return text;

        int endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new InvalidOperationException(
                $"Found patch start marker '{start}' but not matching end marker '{end}'."
            );
        }

        endIndex += end.Length;

        while (endIndex < text.Length && (text[endIndex] == '\r' || text[endIndex] == '\n'))
        {
            endIndex++;
        }

        return text.Remove(startIndex, endIndex - startIndex);
    }

    private static string ReplaceMarkedBlock(string text, string start, string end, string replacement)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0) return text;

        int endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new InvalidOperationException(
                $"Found patch start marker '{start}' but not matching end marker '{end}'."
            );
        }

        endIndex += end.Length;

        return text.Substring(0, startIndex) +
               replacement +
               text.Substring(endIndex);
    }

    // -------------------------------------------------------------------------
    // BACKUP / RESTORE
    // -------------------------------------------------------------------------

    private void CreateBackup()
    {
        if (!ValidateRuntimeItemPath()) return;

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string backupFolder = Path.Combine(backupRootFolder, timestamp);

        Directory.CreateDirectory(backupFolder);

        string backupPath = Path.Combine(backupFolder, "RuntimeItem.cs.backup");
        File.Copy(runtimeItemPath, backupPath, true);

        AssetDatabase.Refresh();

        Debug.Log($"RuntimeItem.cs backup created at: {backupPath}");
    }

    private void RestoreLatestBackupWithDialog()
    {
        if (!ValidateRuntimeItemPath()) return;

        /*
            Important:
            ----------
            We prefer a VANILLA backup: one that does not contain this patch's marker.
            This avoids accidentally restoring a previously patched backup.
        */

        string latestBackup = FindLatestVanillaRuntimeItemBackup();

        if (string.IsNullOrEmpty(latestBackup))
        {
            latestBackup = FindLatestRuntimeItemBackup();
        }

        if (string.IsNullOrEmpty(latestBackup))
        {
            EditorUtility.DisplayDialog(
                "No Backups Found",
                "No RuntimeItem.cs.backup files were found in the configured backup folder or anywhere under Assets.",
                "OK"
            );
            return;
        }

        bool backupIsVanilla = IsRuntimeItemBackupVanilla(latestBackup);

        if (!EditorUtility.DisplayDialog(
                "Restore Backup?",
                $"Restore RuntimeItem.cs from:\n\n{latestBackup}\n\n" +
                $"Backup type: {(backupIsVanilla ? "Vanilla / unpatched" : "Patched or unknown")}",
                "Restore",
                "Cancel"))
        {
            return;
        }

        File.Copy(latestBackup, runtimeItemPath, true);
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "Backup Restored",
            "RuntimeItem.cs was restored from backup.",
            "OK"
        );
    }

    private string FindLatestRuntimeItemBackup()
    {
        /*
            Fallback finder:
            ----------------
            Returns the newest RuntimeItem.cs.backup, whether vanilla or patched.
            Used only if no vanilla backup can be found.
        */

        return FindAllRuntimeItemBackups()
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
    }

    private string FindLatestVanillaRuntimeItemBackup()
    {
        /*
            A vanilla backup is preferred because it does not contain our patch marker.
            This protects against the common situation where a backup was created while
            RuntimeItem.cs was already patched.
        */

        string[] allBackups = FindAllRuntimeItemBackups();

        return allBackups
            .Where(IsRuntimeItemBackupVanilla)
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
    }

    private string[] FindAllRuntimeItemBackups()
    {
        /*
            Search both the configured backup folder and the whole Assets folder.
            Distinct() avoids duplicates if the configured backup folder is inside Assets.
        */

        string[] configuredFolderMatches = Directory.Exists(backupRootFolder)
            ? Directory.GetFiles(backupRootFolder, "RuntimeItem.cs.backup", SearchOption.AllDirectories)
            : Array.Empty<string>();

        string[] wholeAssetsMatches = Directory.GetFiles(
            Application.dataPath,
            "RuntimeItem.cs.backup",
            SearchOption.AllDirectories
        );

        return configuredFolderMatches
            .Concat(wholeAssetsMatches)
            .Distinct()
            .ToArray();
    }

    private bool IsRuntimeItemBackupVanilla(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!File.Exists(path)) return false;

        string text = File.ReadAllText(path);

        return
            text.Contains("namespace GameCreator.Runtime.Inventory") &&
            text.Contains("public class RuntimeItem") &&
            !text.Contains(PATCH_ID);
    }

    // -------------------------------------------------------------------------
    // OPTIONAL GENERATED HELPER SCRIPT
    // -------------------------------------------------------------------------

    private void GenerateHelperScript()
    {
        Directory.CreateDirectory(generatedScriptsFolder);

        string path = Path.Combine(
            generatedScriptsFolder,
            "RuntimeItemOverrideUtility.cs"
        );

        File.WriteAllText(path, HelperScriptSource, Encoding.UTF8);
        AssetDatabase.Refresh();

        Debug.Log($"Generated helper script at: {path}");
    }

    // -------------------------------------------------------------------------
    // PATH HELPERS
    // -------------------------------------------------------------------------

    private bool ValidateRuntimeItemPath()
    {
        if (string.IsNullOrWhiteSpace(runtimeItemPath))
        {
            EditorUtility.DisplayDialog(
                "Missing Path",
                "Please select or auto-find RuntimeItem.cs first.",
                "OK"
            );
            return false;
        }

        if (!File.Exists(runtimeItemPath))
        {
            EditorUtility.DisplayDialog(
                "Invalid Path",
                "The selected RuntimeItem.cs file does not exist.",
                "OK"
            );
            return false;
        }

        return true;
    }

    private void AutoFindRuntimeItem()
    {
        /*
            Purpose:
            --------
            Finds the correct Game Creator Inventory RuntimeItem.cs.

            Future-proofing:
            ----------------
            We no longer require only exact 'public int Weight' and 'public int Price'
            matches. We score likely files based on several RuntimeItem-specific features.
        */

        string[] guids = AssetDatabase.FindAssets("RuntimeItem t:Script");

        string bestPath = null;
        int bestScore = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);

            if (!Path.GetFileName(path).Equals("RuntimeItem.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(path);
            int score = 0;

            if (text.Contains("namespace GameCreator.Runtime.Inventory")) score += 5;
            if (text.Contains("public class RuntimeItem")) score += 5;
            if (text.Contains("RuntimeProperties")) score += 2;
            if (text.Contains("RuntimeSockets")) score += 2;
            if (text.Contains("InventoryRepository")) score += 2;
            if (text.Contains("public int Weight")) score += 2;
            if (text.Contains("public int Price")) score += 2;
            if (text.Contains(PATCH_ID)) score += 3;

            if (score > bestScore)
            {
                bestScore = score;
                bestPath = path;
            }
        }

        if (!string.IsNullOrEmpty(bestPath) && bestScore >= 10)
        {
            runtimeItemPath = bestPath;
            Repaint();

            EditorUtility.DisplayDialog(
                "RuntimeItem.cs Found",
                $"Selected likely RuntimeItem.cs:\n\n{runtimeItemPath}",
                "OK"
            );

            return;
        }

        EditorUtility.DisplayDialog(
            "RuntimeItem.cs Not Found",
            "Could not confidently find Game Creator 2's RuntimeItem.cs. Please paste the path manually.",
            "OK"
        );
    }

    private void PingRuntimeItemFile()
    {
        if (!ValidateRuntimeItemPath()) return;

        UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(runtimeItemPath);
        if (asset == null) return;

        EditorGUIUtility.PingObject(asset);
        Selection.activeObject = asset;
    }

    // -------------------------------------------------------------------------
    // ORIGINAL SOURCE BLOCKS FROM UPLOADED RUNTIMEITEM.CS
    // -------------------------------------------------------------------------

    private const string OriginalPriceProperty =
@"        public int Price => Inventory.Price.GetValue(this);";

    private const string OriginalWeightProperty =
@"        public int Weight
        {
            get
            {
                int weight = this.Item.Shape.Weight;
                if (this.Bag == null) return weight;
                
                foreach (KeyValuePair<IdString, RuntimeSocket> entry in this.m_Sockets)
                {
                    if (!entry.Value.HasAttachment) continue;
                    weight += entry.Value.Attachment.Weight;
                }

                return weight;
            }
        }";

    // -------------------------------------------------------------------------
    // PATCHED SOURCE BLOCKS
    // -------------------------------------------------------------------------

    private const string PatchedFields =
@"        " + FIELDS_START + @"
        /*
            These fields are intentionally stored directly on RuntimeItem.

            Reason:
            -------
            RuntimeProperties are useful for normal GC2 item properties, but the uploaded
            RuntimeProperty class does not expose a simple public constructor for creating
            arbitrary new runtime-only property IDs. Direct serialized fields are therefore
            safer, clearer and faster.

            Serialization:
            --------------
            RuntimeItem is already [Serializable]. These fields should therefore travel
            with the RuntimeItem in the same way as the existing RuntimeItem fields, subject
            to whatever GC2 save/load token system serializes for RuntimeItem in your project.
        */

        [SerializeField] private bool m_HasRuntimePriceOverride;
        [SerializeField] private int m_RuntimePriceOverride;

        [SerializeField] private bool m_HasRuntimeWeightOverride;
        [SerializeField] private int m_RuntimeWeightOverride;
        " + FIELDS_END;

    private const string PatchedMethods =
@"        " + METHODS_START + @"
        // RUNTIME PRICE / WEIGHT OVERRIDE API: --------------------------------------------------
        //
        // These methods provide a small, explicit public API that custom GC2 Actions,
        // Conditions, property getters, debugging tools and gameplay systems can call.
        //
        // The actual existing GC2 UI integration happens because the Price and Weight
        // properties below are patched to honour these override values.

        public bool HasRuntimePriceOverride => this.m_HasRuntimePriceOverride;

        public int RuntimePriceOverride => this.m_RuntimePriceOverride;

        public bool HasRuntimeWeightOverride => this.m_HasRuntimeWeightOverride;

        public int RuntimeWeightOverride => this.m_RuntimeWeightOverride;

        public void SetRuntimePriceOverride(int value)
        {
            this.m_HasRuntimePriceOverride = true;
            this.m_RuntimePriceOverride = Mathf.Max(0, value);
        }

        public void ClearRuntimePriceOverride()
        {
            this.m_HasRuntimePriceOverride = false;
            this.m_RuntimePriceOverride = 0;
        }

        public bool TryGetRuntimePriceOverride(out int value)
        {
            value = this.m_RuntimePriceOverride;
            return this.m_HasRuntimePriceOverride;
        }

        public void SetRuntimeWeightOverride(int value)
        {
            this.m_HasRuntimeWeightOverride = true;
            this.m_RuntimeWeightOverride = Mathf.Max(0, value);
        }

        public void ClearRuntimeWeightOverride()
        {
            this.m_HasRuntimeWeightOverride = false;
            this.m_RuntimeWeightOverride = 0;
        }

        public bool TryGetRuntimeWeightOverride(out int value)
        {
            value = this.m_RuntimeWeightOverride;
            return this.m_HasRuntimeWeightOverride;
        }
        " + METHODS_END;

    private const string PatchedPriceProperty =
@"        " + PRICE_START + @"
        public int Price
        {
            get
            {
                /*
                    If a runtime price override has been set, return it.

                    This is the key hook that makes existing GC2 UI and systems automatically
                    honour the override, because existing GC2 code already asks RuntimeItem.Price.
                */
                return this.TryGetRuntimePriceOverride(out int runtimePriceOverride)
                    ? runtimePriceOverride
                    : Inventory.Price.GetValue(this);
            }
        }
        " + PRICE_END;

    private const string PatchedWeightProperty =
@"        " + WEIGHT_START + @"
        public int Weight
        {
            get
            {
                /*
                    If a runtime weight override has been set, return it.

                    This means Bag UI, Bag weight calculations and any other GC2 system that
                    reads RuntimeItem.Weight should automatically see the overridden value.
                */
                if (this.TryGetRuntimeWeightOverride(out int runtimeWeightOverride))
                {
                    return runtimeWeightOverride;
                }

                int weight = this.Item.Shape.Weight;
                if (this.Bag == null) return weight;
                
                foreach (KeyValuePair<IdString, RuntimeSocket> entry in this.m_Sockets)
                {
                    if (!entry.Value.HasAttachment) continue;
                    weight += entry.Value.Attachment.Weight;
                }

                return weight;
            }
        }
        " + WEIGHT_END;




    // -------------------------------------------------------------------------
    // OPTIONAL HELPER SCRIPT SOURCE
    // -------------------------------------------------------------------------

    private const string InstructionSetPriceSource =
@"using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace GameCreator.Runtime.Inventory
{
    [Version(1, 0, 0)]

    [Title(""Set Runtime Item Price Override"")]
    [Description(
        ""Sets a per-runtime-item price override. Requires RuntimeItem.cs to have been patched "" +
        ""with the runtime price/weight override patch. Existing GC2 UI and systems that read "" +
        ""RuntimeItem.Price should then honour this value.""
    )]

    [Category(""Inventory/Runtime Item/Set Runtime Item Price Override"")]

    [Parameter(""Runtime Item"", ""The specific RuntimeItem instance whose runtime price should be overridden."")]
    [Parameter(""Price"", ""The new effective runtime price. Values below zero are clamped to zero."")]

    [Keywords(""Inventory"", ""Item"", ""Runtime"", ""Price"", ""Cost"", ""Value"", ""Override"", ""Merchant"", ""Shop"")]
    [Image(typeof(IconItem), ColorTheme.Type.Green)]

    [Serializable]
    public class InstructionRuntimeItemSetPriceOverride : Instruction
    {
        [SerializeField] private PropertyGetRuntimeItem m_RuntimeItem = new PropertyGetRuntimeItem();
        [SerializeField] private PropertyGetDecimal m_Price = new PropertyGetDecimal(0);

        public override string Title => $""Set {this.m_RuntimeItem} Runtime Price = {this.m_Price}"";

        protected override Task Run(Args args)
        {
            RuntimeItem runtimeItem = this.m_RuntimeItem.Get(args);
            if (runtimeItem == null) return DefaultResult;

            int price = Mathf.Max(0, Mathf.RoundToInt((float)this.m_Price.Get(args)));
            runtimeItem.SetRuntimePriceOverride(price);

            return DefaultResult;
        }
    }
}";

    private const string InstructionSetWeightSource =
    @"using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace GameCreator.Runtime.Inventory
{
    [Version(1, 0, 0)]

    [Title(""Set Runtime Item Weight Override"")]
    [Description(
        ""Sets a per-runtime-item weight override. Requires RuntimeItem.cs to have been patched "" +
        ""with the runtime price/weight override patch. Existing Bag weight calculations that read "" +
        ""RuntimeItem.Weight should then honour this value.""
    )]

    [Category(""Inventory/Runtime Item/Set Runtime Item Weight Override"")]

    [Parameter(""Runtime Item"", ""The specific RuntimeItem instance whose runtime weight should be overridden."")]
    [Parameter(""Weight"", ""The new effective runtime weight. Values below zero are clamped to zero."")]

    [Keywords(""Inventory"", ""Item"", ""Runtime"", ""Weight"", ""Encumbrance"", ""Override"", ""Bag"")]
    [Image(typeof(IconWeight), ColorTheme.Type.Green)]

    [Serializable]
    public class InstructionRuntimeItemSetWeightOverride : Instruction
    {
        [SerializeField] private PropertyGetRuntimeItem m_RuntimeItem = new PropertyGetRuntimeItem();
        [SerializeField] private PropertyGetDecimal m_Weight = new PropertyGetDecimal(0);

        public override string Title => $""Set {this.m_RuntimeItem} Runtime Weight = {this.m_Weight}"";

        protected override Task Run(Args args)
        {
            RuntimeItem runtimeItem = this.m_RuntimeItem.Get(args);
            if (runtimeItem == null) return DefaultResult;

            int weight = Mathf.Max(0, Mathf.RoundToInt((float)this.m_Weight.Get(args)));
            runtimeItem.SetRuntimeWeightOverride(weight);

            return DefaultResult;
        }
    }
}";

    private const string GetPriceSource =
    @"using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Inventory
{
    [Title(""Runtime Item Price"")]
    [Category(""Inventory/Runtime Item Price"")]

    [Image(typeof(IconItem), ColorTheme.Type.Green)]
    [Description(""Returns the effective RuntimeItem.Price, including any runtime override."")]

    [Parameter(""Runtime Item"", ""The RuntimeItem instance whose effective price is read."")]

    [Keywords(""Inventory"", ""Runtime"", ""Item"", ""Price"", ""Cost"", ""Value"", ""Merchant"", ""Shop"", ""Override"")]

    [Serializable]
    public class GetDecimalRuntimeItemPrice : PropertyTypeGetDecimal
    {
        [SerializeField] private PropertyGetRuntimeItem m_RuntimeItem = new PropertyGetRuntimeItem();

        public override double Get(Args args)
        {
            RuntimeItem runtimeItem = this.m_RuntimeItem.Get(args);
            return runtimeItem != null ? runtimeItem.Price : 0d;
        }

        public override string String => $""{this.m_RuntimeItem} Price"";
    }
}";

    private const string GetWeightSource =
    @"using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Inventory
{
    [Title(""Runtime Item Weight"")]
    [Category(""Inventory/Runtime Item Weight"")]

    [Image(typeof(IconWeight), ColorTheme.Type.Green)]
    [Description(""Returns the effective RuntimeItem.Weight, including any runtime override."")]

    [Parameter(""Runtime Item"", ""The RuntimeItem instance whose effective weight is read."")]

    [Keywords(""Inventory"", ""Runtime"", ""Item"", ""Weight"", ""Encumbrance"", ""Bag"", ""Override"")]

    [Serializable]
    public class GetDecimalRuntimeItemWeight : PropertyTypeGetDecimal
    {
        [SerializeField] private PropertyGetRuntimeItem m_RuntimeItem = new PropertyGetRuntimeItem();

        public override double Get(Args args)
        {
            RuntimeItem runtimeItem = this.m_RuntimeItem.Get(args);
            return runtimeItem != null ? runtimeItem.Weight : 0d;
        }

        public override string String => $""{this.m_RuntimeItem} Weight"";
    }
}";

    private const string HelperScriptSource =
@"using GameCreator.Runtime.Inventory;

namespace GameCreator.Runtime.Inventory
{
    /*
        RuntimeItemOverrideUtility
        ----------------------------------------------------------------------

        Optional convenience helpers for working with the patched RuntimeItem API.

        These helpers are deliberately small. The real runtime behaviour comes from
        the patched RuntimeItem.Price and RuntimeItem.Weight getters.
    */

    public static class RuntimeItemOverrideUtility
    {
        public static void SetRuntimePrice(RuntimeItem runtimeItem, int price)
        {
            if (runtimeItem == null) return;
            runtimeItem.SetRuntimePriceOverride(price);
        }

        public static void ClearRuntimePrice(RuntimeItem runtimeItem)
        {
            if (runtimeItem == null) return;
            runtimeItem.ClearRuntimePriceOverride();
        }

        public static int GetRuntimePrice(RuntimeItem runtimeItem)
        {
            return runtimeItem != null ? runtimeItem.Price : 0;
        }

        public static void SetRuntimeWeight(RuntimeItem runtimeItem, int weight)
        {
            if (runtimeItem == null) return;
            runtimeItem.SetRuntimeWeightOverride(weight);
        }

        public static void ClearRuntimeWeight(RuntimeItem runtimeItem)
        {
            if (runtimeItem == null) return;
            runtimeItem.ClearRuntimeWeightOverride();
        }

        public static int GetRuntimeWeight(RuntimeItem runtimeItem)
        {
            return runtimeItem != null ? runtimeItem.Weight : 0;
        }
    }
}";

}

#endif