using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace LowEndGames.TextureBrowser
{
    public class TextureBrowserWindow : EditorWindow
    {
        [SerializeField] private StyleSheet m_styleSheet;

        private const string TextureDirectoryKey = "TextureBrowser_TextureDirectory";
        private const string MaterialDirectoryKey = "TextureBrowser_MaterialDirectory";
        private const string ThumbnailSizeKey = "TextureBrowser_ThumbnailSize";
        private const string DefaultShaderKey = "TextureBrowser_DefaultShader";
        private const string ShaderMainTexKeywordKey = "TextureBrowser_DefaultShaderMainTexKeyword";

        private const string DefaultTextureDirectory = "Assets/Textures";
        private const string DefaultMaterialDirectory = "Assets/Materials";
        private const int DefaultThumbnailSize = 4;
        private const string DefaultShaderName = "Standard";
        private const string DefaultShaderMainTexKeyword = "_BaseMap";

        private static string TextureDirectory => EditorPrefs.GetString(TextureDirectoryKey, DefaultTextureDirectory);
        private static string MaterialDirectory => EditorPrefs.GetString(MaterialDirectoryKey, DefaultMaterialDirectory);
        private static string DefaultShader => EditorPrefs.GetString(DefaultShaderKey, DefaultShaderName);
        private static int ThumbnailSize => EditorPrefs.GetInt(ThumbnailSizeKey, DefaultThumbnailSize);

        private Vector2 m_scroll;
        private VisualElement m_textureList;
        private Texture2D m_draggedTexture;
        private Vector2 m_dragStartPosition;
        private string m_searchString;
        private readonly List<Texture2D> m_textures = new();
        private readonly List<Material> m_materials = new();
        private Texture2D m_materialIcon;

        [MenuItem("Low End Games/Texture Browser")]
        private static void Open()
        {
            var window = GetWindow<TextureBrowserWindow>();
            var title = EditorGUIUtility.IconContent("Texture Icon", "Texture Browser");
            title.text = "Texture Browser";
            window.titleContent = title;
            window.Show();
        }
        
        private void OnEnable()
        {
            m_materialIcon = EditorGUIUtility.IconContent("Material Icon").image as Texture2D;
        }
        
        private void CreateGUI()
        {
            var root = rootVisualElement;

            root.styleSheets.Add(m_styleSheet);
            root.AddToClassList("container");
            
            // toolbar

            var toolbar = new Toolbar().AddTo(root);
            new ToolbarSearchField { value = m_searchString }
                .WithClasses("search-field")
                .AddTo(toolbar)
                .RegisterValueChangedCallback((e) =>
                {
                    m_searchString = e.newValue;
                    PopulateList();
                });

            new ToolbarButton(() =>
                {
                    RefreshTextures();
                    PopulateList();

                })
                {
                    iconImage = EditorGUIUtility.IconContent("Refresh").image as Texture2D,
                    tooltip = "Refresh the window contents"
                }
                .AddTo(toolbar);
            
            // texture grid

            m_textureList = new ScrollView(ScrollViewMode.Vertical).AddTo(root);
            m_textureList.AddToClassList("texture-list-container");
            m_textureList.contentContainer.AddToClassList("texture-list");
            
            // footer 

            var footer = new Toolbar().AddTo(root);
            
            new SliderInt { value = ThumbnailSize, lowValue = 1, highValue = 4 }
                .WithClasses("size-slider")
                .AddTo(footer)
                .RegisterValueChangedCallback(OnSizeChange);

            new ToolbarButton(() => { SettingsService.OpenUserPreferences("Preferences/Texture Browser"); })
                {
                    iconImage = EditorGUIUtility.IconContent("Settings Icon").image as Texture2D,
                    tooltip = "Settings"
                }
                .AddTo(footer);
            
            RefreshTextures();
            CacheMaterials();
            PopulateList();
        }

        private void OnSizeChange(ChangeEvent<int> evt)
        {
            EditorPrefs.SetInt(ThumbnailSizeKey, evt.newValue);

            if (m_textureList != null)
            {
                foreach (var e in m_textureList.Children())
                {
                    e.style.width = ThumbnailSize * 64;
                    e.style.height = ThumbnailSize * 64;
                }
            }
        }
        
        private void RefreshTextures()
        {
            var guids = AssetDatabase.FindAssets("t:texture2d", new[] { TextureDirectory });

            m_textures.Clear();
            
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex)
                {
                    m_textures.Add(tex);
                }
            }
        }

        private void PopulateList()
        {
            if (m_textureList == null)
            {
                return;
            }
            
            m_textureList.Clear();

            foreach (var tex in m_textures)
            {
                // filter 
                
                if (!string.IsNullOrEmpty(m_searchString) &&
                    !tex.name.Contains(m_searchString, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;   
                }
                
                var texElement = new VisualElement().WithClasses("tex-parent");
                texElement.style.backgroundImage = tex;
                texElement.style.width = ThumbnailSize * 64;
                texElement.style.height = ThumbnailSize * 64;
                
                var label = new Label { text = tex.name }.WithClasses("tex-label");
                texElement.Add(label);
                
                // button to ping material
                
                new Button(() =>
                    {
                        var material = FindOrCreateMaterial(tex);
                        EditorGUIUtility.PingObject(material);
                    
                    }){ iconImage = m_materialIcon, tooltip = "Locate Material In Project"}
                    .WithClasses("tex-mat-button")
                    .AddTo(texElement);
                
                // start drag on mouse down

                texElement.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button == 0) // Left mouse button
                    {
                        m_draggedTexture = tex;
                        m_dragStartPosition = evt.localMousePosition;
                    }
                });

                texElement.RegisterCallback<MouseMoveEvent>(evt =>
                {
                    // start drag after a small movement
                    
                    if (m_draggedTexture != null && Vector2.Distance(evt.localMousePosition, m_dragStartPosition) > 5)
                    {
                        var material = FindOrCreateMaterial(m_draggedTexture);

                        DragAndDrop.PrepareStartDrag();
                        DragAndDrop.objectReferences = new Object[] { material };
                        DragAndDrop.StartDrag(material.name);
                        m_draggedTexture = null; // Reset dragged texture
                    }
                });
                
                // Reset dragged texture on mouse up

                texElement.RegisterCallback<MouseUpEvent>(evt =>
                {
                    m_draggedTexture = null; 
                });

                m_textureList.Add(texElement);
            }
        }

        private void CacheMaterials()
        {
            m_materials.Clear();
            
            var guids = AssetDatabase.FindAssets("t:material", new[] { MaterialDirectory });

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (material)
                {
                    m_materials.Add(material);
                }
            }
        }

        private Material FindOrCreateMaterial(Texture2D texture2D)
        {
            var mainTexKeyword = EditorPrefs.GetString(ShaderMainTexKeywordKey, DefaultShaderMainTexKeyword);

            foreach (var material in m_materials)
            {
                if (material.HasProperty(mainTexKeyword) && material.GetTexture(mainTexKeyword) == texture2D)
                {
                    return material;
                }
            }

            var shader = Shader.Find(DefaultShader);

            var newMaterial = new Material(shader);

            newMaterial.SetTexture(mainTexKeyword, texture2D);
            
            var materialsRoot = MaterialDirectory;

            if (!AssetDatabase.IsValidFolder(materialsRoot + "/AutoGenerated"))
                AssetDatabase.CreateFolder(materialsRoot, "AutoGenerated");

            var materialName = "mat." + texture2D.name.ToLower().Replace(" ", "_") + ".mat";

            var relativePath = Path.Combine(materialsRoot + "/AutoGenerated", materialName);
            AssetDatabase.CreateAsset(newMaterial, relativePath);
            AssetDatabase.ImportAsset(relativePath);

            Debug.Log($"[Texture Browser]: Created material at {AssetDatabase.GetAssetPath(newMaterial)}");
            
            m_materials.Add(newMaterial);

            return newMaterial;
        }

        [SettingsProvider]
        private static SettingsProvider CreateSettingsProvider()
        {
            var textureDirectory = EditorPrefs.GetString(TextureDirectoryKey, DefaultTextureDirectory);
            var materialDirectory = EditorPrefs.GetString(MaterialDirectoryKey, DefaultMaterialDirectory);
            var defaultShaderName = EditorPrefs.GetString(DefaultShaderKey, DefaultShaderName);
            var shaderKeyword = EditorPrefs.GetString(ShaderMainTexKeywordKey, DefaultShaderMainTexKeyword);
            var defaultShaderObject = Shader.Find(defaultShaderName);

            var provider = new SettingsProvider("Preferences/Texture Browser", SettingsScope.User)
            {
                label = "Texture Browser",
                guiHandler = _ =>
                {
                    EditorGUILayout.BeginVertical();

                    EditorGUI.BeginChangeCheck();
                    textureDirectory = EditorGUILayout.TextField("Texture Directory", textureDirectory);

                    if (EditorGUI.EndChangeCheck())
                    {
                        if (Directory.Exists(textureDirectory))
                        {
                            EditorPrefs.SetString(TextureDirectoryKey, textureDirectory);
                        }
                        else
                        {
                            EditorGUILayout.HelpBox("Please select a valid folder.", MessageType.Error);
                        }
                    }

                    EditorGUI.BeginChangeCheck();
                    materialDirectory = EditorGUILayout.TextField("Material Directory", materialDirectory);

                    if (EditorGUI.EndChangeCheck())
                    {
                        if (Directory.Exists(materialDirectory))
                        {
                            EditorPrefs.SetString(MaterialDirectoryKey, materialDirectory);
                        }
                        else
                        {
                            EditorGUILayout.HelpBox("Please select a valid folder.", MessageType.Error);
                        }
                    }
                    
                    EditorGUI.BeginChangeCheck();
                    defaultShaderObject = EditorGUILayout.ObjectField("Default Shader", defaultShaderObject, typeof(Shader), false) as Shader;
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (defaultShaderObject != null)
                        {
                            var shaderName = defaultShaderObject.name;
                            if (shaderName != EditorPrefs.GetString(DefaultShaderKey, DefaultShaderName))
                            {
                                EditorPrefs.SetString(DefaultShaderKey, shaderName);
                            }
                        }
                        else
                        {
                            EditorPrefs.SetString(DefaultShaderKey, DefaultShaderName);
                            defaultShaderObject = Shader.Find(DefaultShaderName);
                        }
                    }
                    
                    EditorGUI.BeginChangeCheck();
                    shaderKeyword = EditorGUILayout.TextField("Main Texture Shader Keyword", shaderKeyword);

                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorPrefs.SetString(DefaultShaderKey, shaderKeyword);
                    }


                    EditorGUILayout.EndVertical();
                },
                keywords = new HashSet<string>(new[] { "texture", "browser", "editor", "tool" })
            };
            return provider;
        }
    }
}

