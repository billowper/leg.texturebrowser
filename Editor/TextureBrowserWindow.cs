using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private const string FavouritesKey = "TextureBrowser_Favourites";

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
        private TextureInfo m_draggedTexture;
        private Vector2 m_dragStartPosition;
        private string m_searchString;
        private Texture2D m_materialIcon;
        private Texture2D m_favouriteIcon;
        private Texture2D m_usedIcon;
        
        private VisualElement m_textureListView;

        private readonly List<TextureInfo> m_textures = new();
        private readonly List<Material> m_materials = new();
        private readonly List<string> m_favourites = new();

        [Flags]
        private enum FilterFlags
        {
            None = 1 << 0,
            Favourites = 1 << 1,
            Used = 1 << 2,
        }
        
        private FilterFlags m_filter = FilterFlags.None;

        private static StyleSheet _styleSheet; // for settings provider

        private class TextureInfo
        {
            public Texture2D Texture { get; }
            public Material Material { get; set; }
            public bool IsFavourite { get; set; }
            public VisualElement Element { get; set; }

            public TextureInfo(Texture2D tex)
            {
                Texture = tex;
            }
        }

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
            _styleSheet = m_styleSheet; // for settings provider, hacky but whatever
            
            m_materialIcon = EditorGUIUtility.IconContent("Material Icon").image as Texture2D;
            m_favouriteIcon = EditorGUIUtility.IconContent("Favorite").image as Texture2D;
            m_usedIcon = EditorGUIUtility.IconContent("d_TerrainInspector.TerrainToolSplat On").image as Texture2D;

            m_favourites.Clear();
            m_favourites.AddRange(EditorPrefs.GetString(FavouritesKey, "").Split(","));
        }

        private void OnDisable()
        {
            // cleanup statics
            _styleSheet = null;
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
                .RegisterValueChangedCallback(e =>
                {
                    m_searchString = e.newValue;
                    PopulateList();
                });

            var faveToggle = new ToolbarToggle()
                {
                    value = m_filter.HasFlag(FilterFlags.Favourites),
                    tooltip = "Show only Favourites"
                }
                .AddTo(toolbar);

            faveToggle.Add(new Image() { image = m_favouriteIcon });
            faveToggle.RegisterValueChangedCallback(e =>
            {
                if (e.newValue)
                {
                    m_filter |= FilterFlags.Favourites;
                }
                else
                {
                    m_filter &= ~FilterFlags.Favourites;
                }

                PopulateList();
            });
            

            var usedToggle = new ToolbarToggle()
                {
                    value = m_filter.HasFlag(FilterFlags.Favourites),
                    tooltip = "Show only textures used in open scenes"
                }
                .AddTo(toolbar);

            usedToggle.Add(new Image() { image = m_usedIcon });
            usedToggle.RegisterValueChangedCallback(e =>
            {
                if (e.newValue)
                {
                    m_filter |= FilterFlags.Used;
                }
                else
                {
                    m_filter &= ~FilterFlags.Used;
                }

                PopulateList();
            });
            
            new ToolbarButton(() =>
                {
                    RefreshTextures();
                    CacheMaterials();
                    PopulateList();
                })
                {
                    iconImage = EditorGUIUtility.IconContent("Refresh").image as Texture2D,
                    tooltip = "Refresh the window contents"
                }
                .AddTo(toolbar);
            
            // texture grid

            m_textureListView = new ScrollView(ScrollViewMode.Vertical).AddTo(root);
            m_textureListView.AddToClassList("texture-list-container");
            m_textureListView.contentContainer.AddToClassList("texture-list");
            
            // footer 

            var footer = new Toolbar().AddTo(root).WithClasses("footer");
            
            new SliderInt { value = ThumbnailSize, lowValue = 1, highValue = 4 }
                .WithClasses("size-slider")
                .AddTo(footer)
                .RegisterValueChangedCallback(OnSizeChange);

            new ToolbarButton(() => { SettingsService.OpenUserPreferences("Preferences/Texture Browser"); })
                {
                    iconImage = EditorGUIUtility.IconContent("Settings").image as Texture2D,
                    tooltip = "Open preferences"
                }
                .AddTo(footer);
            
            RefreshTextures();
            CacheMaterials();
            PopulateList();
        }

        private void OnSizeChange(ChangeEvent<int> evt)
        {
            EditorPrefs.SetInt(ThumbnailSizeKey, evt.newValue);

            foreach (var e in m_textures)
            {
                if (e.Element != null)
                {
                    e.Element.style.width = ThumbnailSize * 64;
                    e.Element.style.height = ThumbnailSize * 64;
                }
            }
        }
        
        private void RefreshTextures()
        {
            m_favourites.Clear();
            m_favourites.AddRange(EditorPrefs.GetString(FavouritesKey, "").Split(","));
            
            var guids = AssetDatabase.FindAssets("t:texture2d", new[] { TextureDirectory });

            m_textures.Clear();
            
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex)
                {
                    var texInfo = new TextureInfo(tex);
                    texInfo.Element = CreateElement(texInfo);
                    texInfo.IsFavourite = m_favourites.Contains(tex.name);
                    m_textures.Add(texInfo);
                }
            }
        }

        private VisualElement CreateElement(TextureInfo texInfo)
        {
            var texElement = new VisualElement().WithClasses("tex-parent");
            
            texElement.name = texInfo.Texture.name; // texture name helps us ID this later
            
            if (texInfo.IsFavourite)
            {
                texElement.WithClasses("tex-favourite");
            }
            
            texElement.style.backgroundImage = texInfo.Texture;
            texElement.style.width = ThumbnailSize * 64;
            texElement.style.height = ThumbnailSize * 64;
            
            new Label { text = texInfo.Texture.name }.WithClasses("tex-label").AddTo(texElement);
            
            // button to ping material
            
            new Button(() =>
                {
                    var material = FindOrCreateMaterial(texInfo);
                    EditorGUIUtility.PingObject(material);
                
                }){ iconImage = m_materialIcon, tooltip = "Locate Material In Project"}
                .WithClasses("tex-mat-button")
                .AddTo(texElement);
            
            // button to toggle favourite
            
            new Button(() =>
                {
                    ToggleFavourite(texInfo);

                }){ iconImage = m_favouriteIcon, tooltip = "Favourite"}
                .WithClasses("tex-fav-button")
                .AddTo(texElement);
            
            // start drag on mouse down

            texElement.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 0) // Left mouse button
                {
                    m_draggedTexture = texInfo;
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
            
            // reset dragged texture on mouse up

            texElement.RegisterCallback<MouseUpEvent>(_ =>
            {
                m_draggedTexture = null; 
            });

            return texElement;
        }

        private void PopulateList()
        {
            m_textureListView.Clear();
            
            foreach (var texInfo in m_textures)
            {
                if (!string.IsNullOrEmpty(m_searchString) &&
                    !texInfo.Texture.name.Contains(m_searchString, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                if (m_filter.HasFlag(FilterFlags.Favourites) && !texInfo.IsFavourite)
                {
                    continue;
                }

                if (m_filter.HasFlag(FilterFlags.Used))
                {
                    if (!IsTextureInUse(texInfo.Texture))
                    {
                        continue;
                    }
                }
                
                if (texInfo.IsFavourite)
                {
                    if (!texInfo.Element.ClassListContains("tex-favourite"))
                        texInfo.Element.AddToClassList("tex-favourite");
                }
                else
                {
                    texInfo.Element.RemoveFromClassList("tex-favourite");
                }
                
                m_textureListView.Add(texInfo.Element);
            }
        }

        private void ToggleFavourite(TextureInfo texInfo)
        {
            texInfo.IsFavourite = !texInfo.IsFavourite;
            
            if (texInfo.IsFavourite)
            {
                m_favourites.Add(texInfo.Texture.name);
            }
            else
            {
                m_favourites.Remove(texInfo.Texture.name);
            }
            
            EditorPrefs.SetString(FavouritesKey, string.Join(",", m_favourites));
            
            PopulateList();
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

        private Material FindOrCreateMaterial(TextureInfo texInfo)
        {
            // if we already have a cached material use that
            
            if (texInfo.Material != null)
            {
                return texInfo.Material;
            }

            if (HasMaterial(texInfo.Texture, out var material))
            {
                texInfo.Material = material;
                return material;
            }

            var shader = Shader.Find(DefaultShader);

            var newMaterial = new Material(shader);
            
            var mainTexKeyword = EditorPrefs.GetString(ShaderMainTexKeywordKey, DefaultShaderMainTexKeyword);

            newMaterial.SetTexture(mainTexKeyword, texInfo.Texture);
            
            var materialsRoot = MaterialDirectory;

            if (!AssetDatabase.IsValidFolder(materialsRoot + "/AutoGenerated"))
                AssetDatabase.CreateFolder(materialsRoot, "AutoGenerated");

            var materialName = "mat." + texInfo.Texture.name.ToLower().Replace(" ", "_") + ".mat";

            var relativePath = Path.Combine(materialsRoot + "/AutoGenerated", materialName);
            AssetDatabase.CreateAsset(newMaterial, relativePath);
            AssetDatabase.ImportAsset(relativePath);

            Debug.Log($"[Texture Browser]: Created material at {AssetDatabase.GetAssetPath(newMaterial)}");
            
            m_materials.Add(newMaterial);

            texInfo.Material = newMaterial;

            return newMaterial;
        }

        private bool HasMaterial(Texture2D texture, out Material material)
        {
            var mainTexKeyword = EditorPrefs.GetString(ShaderMainTexKeywordKey, DefaultShaderMainTexKeyword);

            foreach (var m in m_materials)
            {
                if (m.HasProperty(mainTexKeyword) && m.GetTexture(mainTexKeyword) == texture)
                {
                    material = m;
                    return true;
                }
            }

            material = null;
            return false;
        }
        
        private bool IsTextureInUse(Texture2D tex)
        {
            if (HasMaterial(tex, out var material))
            {
                var renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);

                foreach (var r in renderers)
                {
                    if (r.sharedMaterials.Contains(material))
                        return true;
                }
            }

            return false;
        }

        [SettingsProvider]
        private static SettingsProvider CreateSettingsProvider()
        {
            var provider = new SettingsProvider("Preferences/Texture Browser", SettingsScope.User)
            {
                label = "Texture Browser",
                activateHandler = (_, rootElement) =>
                {
                    rootElement.styleSheets.Add(_styleSheet);
                    rootElement.WithClasses("settings-container");
                    
                    var textureDirectory = EditorPrefs.GetString(TextureDirectoryKey, DefaultTextureDirectory);
                    var materialDirectory = EditorPrefs.GetString(MaterialDirectoryKey, DefaultMaterialDirectory);
                    var defaultShaderName = EditorPrefs.GetString(DefaultShaderKey, DefaultShaderName);
                    var shaderKeyword = EditorPrefs.GetString(ShaderMainTexKeywordKey, DefaultShaderMainTexKeyword);
                    var defaultShaderObject = Shader.Find(defaultShaderName);

                    rootElement.Add(new Label("Texture Browser").WithClasses("settings-title"));

                    new TextField("Texture Directory")
                        {
                            value = textureDirectory
                        }
                        .AddTo(rootElement)
                        .RegisterValueChangedCallback(evt =>
                        {
                            if (Directory.Exists(evt.newValue))
                            {
                                EditorPrefs.SetString(TextureDirectoryKey, evt.newValue);
                            }
                        });
                    
                    new TextField("Material Directory")
                        {
                            value = materialDirectory
                        }
                        .AddTo(rootElement)
                        .RegisterValueChangedCallback(evt =>
                        {
                            if (Directory.Exists(evt.newValue))
                            {
                                EditorPrefs.SetString(MaterialDirectoryKey, evt.newValue);
                            }
                        });

                    new ObjectField("Default Shader")
                        {
                            value = defaultShaderObject, objectType = typeof(Shader)
                        }
                        .AddTo(rootElement)
                        .RegisterValueChangedCallback(evt =>
                        {
                            if (evt.newValue != null)
                            {
                                var shaderName = (evt.newValue as Shader)?.name;
                                if (shaderName != EditorPrefs.GetString(DefaultShaderKey, DefaultShaderName))
                                {
                                    EditorPrefs.SetString(DefaultShaderKey, shaderName);
                                }
                            }
                            else
                            {
                                EditorPrefs.SetString(DefaultShaderKey, DefaultShaderName);
                            }
                        });

                    new TextField("Main Texture Shader Keyword")
                        {
                            value = shaderKeyword
                        }
                        .AddTo(rootElement)
                        .RegisterValueChangedCallback(evt =>
                        {
                            EditorPrefs.SetString(ShaderMainTexKeywordKey, evt.newValue);
                        });
                    
                    new Label().AddTo(rootElement);
                },

                keywords = new HashSet<string>(new[] { "texture", "browser", "editor", "tool" })
            };
            
            return provider;
        }
    }
}

