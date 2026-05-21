using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Unity.CodeEditor;

namespace Antigravity.Editor // <--- NEW NAMESPACE
{
    [InitializeOnLoad]
    public class AntigravityScriptEditor : IExternalCodeEditor // <--- NEW CLASS NAME
    {
        const string antigravity_argument = "antigravity_arguments"; // Unique pref keys
        const string antigravity_extension = "antigravity_userExtensions";
        
        static readonly GUIContent k_ResetArguments = EditorGUIUtility.TrTextContent("Reset argument");
        string m_Arguments;

        IDiscovery m_Discoverability;
        IGenerator m_ProjectGeneration;

        static readonly string[] k_SupportedFileNames = { 
            "antigravityide.exe", 
            "antigravity.exe",
            "antigravityide.app",
            "antigravity.app", 
            "antigravityide",
            "antigravity-ide",
            "antigravity"
        };

        static bool IsOSX => Application.platform == RuntimePlatform.OSXEditor;
        static string DefaultApp => EditorPrefs.GetString("kScriptsDefaultApp");
        static string DefaultArgument { get; } = "\"$(ProjectPath)\" -g \"$(File)\":$(Line):$(Column)";

        string Arguments
        {
            get => m_Arguments ?? (m_Arguments = EditorPrefs.GetString(antigravity_argument, DefaultArgument));
            set
            {
                m_Arguments = value;
                EditorPrefs.SetString(antigravity_argument, value);
            }
        }

        static string[] defaultExtensions
        {
            get
            {
                var customExtensions = new[] { "json", "asmdef", "log" };
                return EditorSettings.projectGenerationBuiltinExtensions
                    .Concat(EditorSettings.projectGenerationUserExtensions)
                    .Concat(customExtensions)
                    .Distinct().ToArray();
            }
        }

        static string[] HandledExtensions
        {
            get
            {
                return HandledExtensionsString
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.TrimStart('.', '*'))
                    .ToArray();
            }
        }

        static string HandledExtensionsString
        {
            get => EditorPrefs.GetString(antigravity_extension, string.Join(";", defaultExtensions));
            set => EditorPrefs.SetString(antigravity_extension, value);
        }

        public bool TryGetInstallationForPath(string editorPath, out CodeEditor.Installation installation)
        {
            var lowerCasePath = editorPath.ToLower();
            var filename = Path.GetFileName(lowerCasePath).Replace(" ", "");
            var installations = Installations;

            // Debugging
            // UnityEngine.Debug.Log($"[Antigravity] Checking: {filename}");

            if (!k_SupportedFileNames.Contains(filename))
            {
                installation = default;
                return false;
            }

            installation = new CodeEditor.Installation
            {
                Name = "Antigravity IDE",
                Path = editorPath
            };

            return true;
        }

        public void OnGUI()
        {
            Arguments = EditorGUILayout.TextField("External Script Editor Args", Arguments);
            if (GUILayout.Button(k_ResetArguments, GUILayout.Width(120)))
            {
                Arguments = DefaultArgument;
            }

            // Force these to be visible since we know we are Antigravity
            EditorGUILayout.LabelField("Generate .csproj files for:");
            EditorGUI.indentLevel++;
            SettingsButton(ProjectGenerationFlag.Embedded, "Embedded packages", "");
            SettingsButton(ProjectGenerationFlag.Local, "Local packages", "");
            SettingsButton(ProjectGenerationFlag.Registry, "Registry packages", "");
            SettingsButton(ProjectGenerationFlag.Git, "Git packages", "");
            SettingsButton(ProjectGenerationFlag.BuiltIn, "Built-in packages", "");
            SettingsButton(ProjectGenerationFlag.Unknown, "Packages from unknown sources", "");
            RegenerateProjectFiles();
            EditorGUI.indentLevel--;
        }

        void RegenerateProjectFiles()
        {
            var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(new GUILayoutOption[] { }));
            rect.width = 252;
            if (GUI.Button(rect, "Regenerate project files"))
            {
                m_ProjectGeneration.Sync();
            }
        }

        void SettingsButton(ProjectGenerationFlag preference, string guiMessage, string toolTip)
        {
            var prevValue = m_ProjectGeneration.AssemblyNameProvider.ProjectGenerationFlag.HasFlag(preference);
            var newValue = EditorGUILayout.Toggle(new GUIContent(guiMessage, toolTip), prevValue);
            if (newValue != prevValue)
            {
                m_ProjectGeneration.AssemblyNameProvider.ToggleProjectGeneration(preference);
            }
        }

        public void CreateIfDoesntExist()
        {
            if (!m_ProjectGeneration.SolutionExists())
            {
                m_ProjectGeneration.Sync();
            }
        }

        public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles, string[] movedFromFiles, string[] importedFiles)
        {
            (m_ProjectGeneration.AssemblyNameProvider as IPackageInfoCache)?.ResetPackageInfoCache();
            m_ProjectGeneration.SyncIfNeeded(addedFiles.Union(deletedFiles).Union(movedFiles).Union(movedFromFiles).ToList(), importedFiles);
        }

        public void SyncAll()
        {
            (m_ProjectGeneration.AssemblyNameProvider as IPackageInfoCache)?.ResetPackageInfoCache();
            AssetDatabase.Refresh();
            m_ProjectGeneration.Sync();
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // Fallback in case of invalid characters
            }
            path = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (path.Length >= 2 && path[1] == ':')
            {
                path = char.ToLowerInvariant(path[0]) + path.Substring(1);
            }
            return path;
        }

        static string GetArgumentsForPath(string template, string projectPath, string filePath, int line, int column)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                string cleanedTemplate = template;
                
                // Strip the -g flag and following file/line/column placeholders
                cleanedTemplate = System.Text.RegularExpressions.Regex.Replace(
                    cleanedTemplate,
                    @"\s*-g\s+""?\$\(File\)""?(?::\$\(Line\))?(?::\$\(Column\))?",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

                cleanedTemplate = cleanedTemplate
                    .Replace("$(File)", "")
                    .Replace("$(Line)", "")
                    .Replace("$(Column)", "");

                cleanedTemplate = cleanedTemplate.Replace("$(ProjectPath)", projectPath);
                return cleanedTemplate.Trim();
            }
            else
            {
                string result = template;
                result = result.Replace("$(ProjectPath)", projectPath);
                result = result.Replace("$(File)", filePath);
                result = result.Replace("$(Line)", line.ToString());
                result = result.Replace("$(Column)", column.ToString());
                return result;
            }
        }

        public bool OpenProject(string path, int line, int column)
        {
            if (path != "" && (!SupportsExtension(path) || !File.Exists(path)))
            {
                return false;
            }

            if (line == -1) line = 1;
            if (column == -1) column = 0;

            string projectDir = NormalizePath(m_ProjectGeneration.ProjectDirectory);
            string fileTarget = string.IsNullOrEmpty(path) ? "" : NormalizePath(path);

            if (fileTarget.Equals(projectDir, StringComparison.OrdinalIgnoreCase))
            {
                fileTarget = "";
            }

            string arguments = GetArgumentsForPath(Arguments, projectDir, fileTarget, line, column);

            if (IsOSX) return OpenOSX(arguments);

            var app = DefaultApp;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = app,
                    Arguments = arguments,
                    WindowStyle = app.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                    CreateNoWindow = true,
                    UseShellExecute = true,
                }
            };

            process.Start();
            return true;
        }

        static bool OpenOSX(string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-n \"{DefaultApp}\" --args {arguments}",
                    UseShellExecute = true,
                }
            };
            process.Start();
            return true;
        }

        static bool SupportsExtension(string path)
        {
            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return false;
            return HandledExtensions.Contains(extension.TrimStart('.'));
        }

        public CodeEditor.Installation[] Installations => m_Discoverability.PathCallback();

        public AntigravityScriptEditor(IDiscovery discovery, IGenerator projectGeneration)
        {
            m_Discoverability = discovery;
            m_ProjectGeneration = projectGeneration;
        }

        static AntigravityScriptEditor()
        {
            // Register as a completely new Editor
            var editor = new AntigravityScriptEditor(new AntigravityDiscovery(), new ProjectGeneration(Directory.GetParent(Application.dataPath).FullName));
            CodeEditor.Register(editor);

            if (IsAntigravityInstallation(CodeEditor.CurrentEditorInstallation))
            {
                editor.CreateIfDoesntExist();
            }
        }

        static bool IsAntigravityInstallation(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var lowerCasePath = path.ToLower();
            var filename = Path.GetFileName(lowerCasePath).Replace(" ", "");
            return k_SupportedFileNames.Contains(filename);
        }

        public void Initialize(string editorInstallationPath) { }
    }
}