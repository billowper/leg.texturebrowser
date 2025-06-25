using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.EditorCoroutines.Editor;
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

        [MenuItem("LEG/Texture Browser")]
        private static void Open()
        {
            var window = GetWindow<TextureBrowserWindow>();
            var title = EditorGUIUtility.IconContent("Texture Icon", "Texture Browser");
            title.text = "Texture Browser";
            window.titleContent = title;
            window.Show();
        }

        private static StyleSheet _styleSheet; // static copy for settings provider

        // window state

        private Vector2 m_scroll;
        private TextureInfo m_draggedTexture;
        private Vector2 m_dragStartPosition;
        private string m_searchString = "";
        private Texture2D m_materialIcon;
        private Texture2D m_favouriteIcon;
        private Texture2D m_usedIcon;
        private VisualElement m_textureListView;
        private VisualElement m_quickSearchView;
        private FilterFlags m_filter = FilterFlags.None;

        // data

        private readonly List<TextureInfo> m_data = new();
        private readonly List<Material> m_materialCache = new();
        private readonly List<string> m_favourites = new();
        private readonly List<string> m_savedSearches = new();
        private ToolbarSearchField m_searchField;
        private Label m_infoLabel;
        private EditorCoroutine m_loadRoutine;
        private ToolbarButton m_refreshButton;
        private int m_loadTaskId;

        [Flags]
        private enum FilterFlags
        {
            None = 1 << 0,
            Favourites = 1 << 1,
            Used = 1 << 2,
        }
        
        private class TextureInfo : IComparable<TextureInfo>
        {
            public string Name { get; }
            public Texture2D Texture { get; }
            public Material Material { get; set; }
            public bool IsFavourite { get; set; }
            public VisualElement Element { get; set; }

            public TextureInfo(string name, Texture2D tex)
            {
                if (name.StartsWith("mat."))
                    name = name.Remove(0, "mat.".Length);
                Name = name;
                Texture = tex;
            }

            public int CompareTo(TextureInfo other)
            {
                if (ReferenceEquals(this, other)) return 0;
                if (other is null) return 1;
                return string.Compare(Name, other.Name, StringComparison.Ordinal);
            }
        }

        private void OnEnable()
        {
            _styleSheet = m_styleSheet; // for settings provider, hacky but whatever
            
            m_materialIcon = EditorGUIUtility.IconContent("Material Icon").image as Texture2D;
            m_favouriteIcon = EditorGUIUtility.IconContent("Favorite").image as Texture2D;
            m_usedIcon = EditorGUIUtility.IconContent("d_TerrainInspector.TerrainToolSplat On").image as Texture2D;
        }

        private void OnDisable()
        {
            // cleanup statics
            _styleSheet = null;
            
            m_data.Clear();
            m_materialCache.Clear();
            m_favourites.Clear();
            m_savedSearches.Clear();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;

            root.styleSheets.Add(m_styleSheet);
            root.AddToClassList("container");
            
            // toolbar

            var toolbar = new Toolbar().AddTo(root).WithClasses("toolbar");
            
            m_searchField = new ToolbarSearchField { value = m_searchString }
                .WithClasses("search-field")
                .AddTo(toolbar);
            
            var saveSearchButton = new ToolbarButton(SaveCurrentSearchTerm)
            {
                tooltip = "Save Search",
                iconImage = EditorGUIUtility.IconContent("Toolbar Plus").image as Texture2D
            }.WithClasses("save-search-button").AddTo(toolbar);
            
            saveSearchButton.SetEnabled(false);
            
            m_searchField.RegisterValueChangedCallback(e =>
            {
                m_searchString = e.newValue;
                saveSearchButton.SetEnabled(!string.IsNullOrEmpty(e.newValue));
                PopulateList();
            });
            
            new ToolbarSpacer().AddTo(toolbar);

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

            m_refreshButton = new ToolbarButton(TryRefresh)
                {
                    iconImage = EditorGUIUtility.IconContent("Refresh").image as Texture2D,
                    tooltip = "Refresh content"
                }
                .AddTo(toolbar);
            
            m_refreshButton.SetEnabled(false);

            // saved search list
            
            m_quickSearchView = new VisualElement().WithClasses("saved-search-list").AddTo(root);
            
            // texture grid

            m_textureListView = new ScrollView(ScrollViewMode.Vertical).AddTo(root);
            m_textureListView.AddToClassList("texture-list-container");
            m_textureListView.contentContainer.AddToClassList("texture-list");
            
            // footer 

            var footer = new Toolbar().AddTo(root).WithClasses("footer");
            
            m_infoLabel = new Label().AddTo(footer).WithClasses("info-label");
            
            new SliderInt { value = ThumbnailSize, lowValue = 1, highValue = 4 }
                .WithClasses("size-slider")
                .AddTo(footer)
                .RegisterValueChangedCallback((evt) =>
                {
                    EditorPrefs.SetInt(ThumbnailSizeKey, evt.newValue);

                    foreach (var e in m_data)
                    {
                        if (e.Element != null)
                        {
                            e.Element.style.width = ThumbnailSize * 64;
                            e.Element.style.height = ThumbnailSize * 64;
                        }
                    }
                });

            new ToolbarButton(() => { SettingsService.OpenUserPreferences("Preferences/Texture Browser"); })
                {
                    iconImage = EditorGUIUtility.IconContent("Settings").image as Texture2D,
                    tooltip = "Open preferences"
                }
                .AddTo(footer);

            TryRefresh();
            return;

            void TryRefresh()
            {
                if (Progress.Exists(m_loadTaskId))
                {
                    return;
                }
                
                m_refreshButton.SetEnabled(false);
                EditorCoroutineUtility.StartCoroutine(FullRefresh(), this);
            }
        }

        private IEnumerator FullRefresh()
        {
            m_loadTaskId = Progress.Start("Refreshing Textures");
            
            LoadQuickSearch();
            LoadFavourites();
            LoadContent();
            PopulateList();

            yield return null;
            
            Progress.Remove(m_loadTaskId);
            
            m_refreshButton.SetEnabled(true);
        }

        #region QUICKSEARCH

        private void LoadQuickSearch()
        {
            m_savedSearches.Clear();
            m_savedSearches.AddRange(EditorPrefs.GetString(SavedSearchesKey, "").Split("|"));

            RefreshQuickSearchContent();
        }

        private void RefreshQuickSearchContent()
        {
            m_quickSearchView.Clear();

            foreach (var search in m_savedSearches)
            {
                var button = new Button().WithClasses("saved-search-button");
                button.RegisterCallback<ClickEvent>(evt =>
                {
                    LoadSearchTerm(search, evt.modifiers == EventModifiers.Shift);

                });
                button.Add(new Label { name = "label", text = search });
                button.Add(new Button(() => DeleteSearchTerm(search)) { name = "delete", tooltip = "Remove", text = "X"});
                m_quickSearchView.Add(button);
            }
        }

        private void SaveCurrentSearchTerm()
        {
            if (string.IsNullOrEmpty(m_searchString))
            {
                return;
            }
            
            m_savedSearches.Add(m_searchString);
                
            EditorPrefs.SetString(SavedSearchesKey, string.Join("|", m_savedSearches));
            
            RefreshQuickSearchContent();
        }

        private void LoadSearchTerm(string searchTerm, bool additive)
        {
            if (additive)
            {
                m_searchField.value += $", {searchTerm}";
            }
            else
            {
                m_searchField.value = searchTerm;
            }
        }
        
        private void DeleteSearchTerm(string searchTerm)
        {
            m_savedSearches.Remove(searchTerm);
            
            EditorPrefs.SetString(SavedSearchesKey, string.Join("|", m_savedSearches));
            
            RefreshQuickSearchContent();
        }
        
        #endregion
        
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
            
            new Label { text = texInfo.Name }.WithClasses("tex-label").AddTo(texElement);
            
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
            
            // rmb context

            texElement.RegisterCallback<ContextClickEvent>(_ =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Find in Project"), false, () =>
                {
                    EditorGUIUtility.PingObject(texInfo.Texture);
                });
                menu.ShowAsContext();
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
            var searchTerms = m_searchString.Replace(", ", ",").Split(",");
            
            m_textureListView.Clear();

            foreach (var texInfo in m_data)
            {
                var matchedSearchTerm = string.IsNullOrEmpty(m_searchString) || searchTerms.Any(term => texInfo.Texture.name.Contains(term, StringComparison.InvariantCultureIgnoreCase));

                if (!matchedSearchTerm)
                {
                    continue;
                }

                if (m_filter.HasFlag(FilterFlags.Favourites) && !texInfo.IsFavourite)
                {
                    continue;
                }

                if (m_filter.HasFlag(FilterFlags.Used))
                {
                    if (!IsTextureInUse(texInfo))
                    {
                        continue;
                    }
                }
                
                if (texInfo.IsFavourite)
                {
                    if (!texInfo.Element.ClassListContains("tex-favourite"))
                    {
                        texInfo.Element.AddToClassList("tex-favourite");
                    }
                }
                else
                {
                    texInfo.Element.RemoveFromClassList("tex-favourite");
                }
                
                m_textureListView.Add(texInfo.Element);
            }

            m_infoLabel.text = $"Showing {m_textureListView.childCount} of {m_data.Count}";
        }
        
        private void LoadFavourites()
        {
            m_favourites.Clear();
            m_favourites.AddRange(EditorPrefs.GetString(FavouritesKey, "").Split(","));
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
        
        private void LoadContent()
        {       
            m_data.Clear();
            m_materialCache.Clear();
            
            var materialGuids = AssetDatabase.FindAssets("t:material", new[] { MaterialDirectory });

            foreach (var guid in materialGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material)
                {
                    if (m_materialCache.Contains(material))
                    {
                        continue;
                    }
                    
                    Texture2D tex = null; 
                    
                    if (material.mainTexture is Texture2D tex2d)
                    {
                        tex = tex2d;
                    }
                    else
                    {
                        var textProps = material.GetTexturePropertyNames();
                        
                        if (textProps.Length > 0 && material.GetTexture(textProps[0]) is Texture2D tex2D)
                            tex = tex2D;
                    }

                    if (tex != null)
                    {
                        var texInfo = new TextureInfo(material.name, tex);
                        texInfo.Element = CreateElement(texInfo);
                        texInfo.IsFavourite = m_favourites.Contains(tex.name);
                        texInfo.Material = material;
                        m_data.Add(texInfo);
                    }

                    m_materialCache.Add(material);
                }
            }
            
            var textureGuids = AssetDatabase.FindAssets("t:texture2d", new[] { TextureDirectory });

            foreach (var guid in textureGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex)
                {
                    if (m_data.Any(d => d.Texture == tex))
                    {
                        // dont allow duplicates for now...
                        continue;
                    }
                    
                    var texInfo = new TextureInfo(tex.name, tex);
                    texInfo.Element = CreateElement(texInfo);
                    texInfo.IsFavourite = m_favourites.Contains(tex.name);
                    texInfo.Material = GetMaterial(tex);
                    m_data.Add(texInfo);
                }
            }
            
            m_data.Sort();

            return;

            Material GetMaterial(Texture2D texture2D)
            {
                foreach (var material in m_materialCache)
                {
                    if (material.mainTexture == texture2D)
                    {
                        return material;
                    }

                    var textProps = material.GetTexturePropertyNames();
                    foreach (var textProp in textProps)
                    {
                        if (material.GetTexture(textProp) == texture2D)
                            return material;
                    }
                }
                
                return null;
            }
        }

        private Material FindOrCreateMaterial(TextureInfo texInfo)
        {
            // if we already have a cached material use that
            
            if (texInfo.Material != null)
            {
                return texInfo.Material;
            }

            var shader = Shader.Find(DefaultShader);
            var newMaterial = new Material(shader);
            var mainTexKeyword = EditorPrefs.GetString(ShaderMainTexKeywordKey, DefaultShaderMainTexKeyword);

            newMaterial.SetTexture(mainTexKeyword, texInfo.Texture);
            
            var materialsRoot = MaterialDirectory;

            if (!AssetDatabase.IsValidFolder(materialsRoot + "/AutoGenerated"))
            {
                AssetDatabase.CreateFolder(materialsRoot, "AutoGenerated");
            }

            var materialName = $"{MaterialPrefix}{texInfo.Texture.name.ToLower().Replace(" ", "_")}.mat";

            var relativePath = Path.Combine(materialsRoot + "/AutoGenerated", materialName);
            AssetDatabase.CreateAsset(newMaterial, relativePath);
            AssetDatabase.ImportAsset(relativePath);

            Debug.Log($"[Texture Browser]: Created material at {AssetDatabase.GetAssetPath(newMaterial)}");
            
            m_materialCache.Add(newMaterial);

            texInfo.Material = newMaterial;

            return newMaterial;
        }

        private bool IsTextureInUse(TextureInfo texInfo)
        {
            if (texInfo.Material)
            {
                var renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);

                foreach (var r in renderers)
                {
                    if (r.sharedMaterials.Contains(texInfo.Material))
                    {
                        return true;
                    }
                }
            }
        
            return false;
        }
        
        #region SETTINGS

        private static string GetPrefsKey(string key)
        {
            return $"{Application.productName}.{key}";
        }

        private static string FavouritesKey => GetPrefsKey("TextureBrowser_Favourites");
        private static string SavedSearchesKey => GetPrefsKey("TextureBrowser_SavedSearches");

        private const string DefaultShaderMainTexKeyword = "_BaseMap";
        private static string ShaderMainTexKeywordKey => GetPrefsKey("TextureBrowser_DefaultShaderMainTexKeyword");

        private static string TextureDirectory => EditorPrefs.GetString(TextureDirectoryKey, DefaultTextureDirectory);
        private static string TextureDirectoryKey => GetPrefsKey("TextureBrowser_TextureDirectory");
        private const string DefaultTextureDirectory = "Assets/Textures";

        private static string MaterialDirectory => EditorPrefs.GetString(MaterialDirectoryKey, DefaultMaterialDirectory);
        private static string MaterialDirectoryKey => GetPrefsKey("TextureBrowser_MaterialDirectory");
        private const string DefaultMaterialDirectory = "Assets/Materials";

        private static string DefaultShader => EditorPrefs.GetString(DefaultShaderKey, DefaultShaderName);
        private static string DefaultShaderKey => GetPrefsKey("TextureBrowser_DefaultShader");
        private const string DefaultShaderName = "Standard";


        private static string MaterialPrefix => EditorPrefs.GetString(MaterialPrefixKey, DefaultMaterialPrefix);
        private static string MaterialPrefixKey => GetPrefsKey("TextureBrowser_MaterialPrefix");
        private const string DefaultMaterialPrefix = "mat_";

        private static int ThumbnailSize => EditorPrefs.GetInt(ThumbnailSizeKey, DefaultThumbnailSize);
        private static string ThumbnailSizeKey => GetPrefsKey("TextureBrowser_ThumbnailSize");
        private const int DefaultThumbnailSize = 4;
        
        [SettingsProvider]
        private static SettingsProvider CreateSettingsProvider()
        {
            var provider = new SettingsProvider("Preferences/Texture Browser", SettingsScope.User)
            {
                label = "Texture Browser",
                activateHandler = (_, rootElement) =>
                {
                    if (_styleSheet != null)
                        rootElement.styleSheets.Add(_styleSheet);
                    
                    rootElement.WithClasses("settings-container");
                    
                    var textureDirectory = EditorPrefs.GetString(TextureDirectoryKey, DefaultTextureDirectory);
                    var materialDirectory = EditorPrefs.GetString(MaterialDirectoryKey, DefaultMaterialDirectory);
                    var defaultShaderName = EditorPrefs.GetString(DefaultShaderKey, DefaultShaderName);
                    var shaderKeyword = EditorPrefs.GetString(ShaderMainTexKeywordKey, DefaultShaderMainTexKeyword);
                    var materialPrefix = EditorPrefs.GetString(MaterialPrefixKey, DefaultMaterialPrefix);
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

                    new TextField("Generated Material Prefix")
                        {
                            value = materialPrefix
                        }
                        .AddTo(rootElement)
                        .RegisterValueChangedCallback(evt =>
                        {
                            EditorPrefs.SetString(MaterialPrefixKey, evt.newValue);
                        });
                    
                    new Label().AddTo(rootElement);
                },

                keywords = new HashSet<string>(new[] { "texture", "browser", "editor", "tool" })
            };
            
            return provider;
        }
        
        #endregion
    }
}

