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

        [MenuItem("Window/Texture Browser")]
        private static void Open()
        {
            var window = GetWindow<TextureBrowserWindow>();
            var title = EditorGUIUtility.IconContent("Texture Icon", "Texture Browser");
            title.text = "Texture Browser";
            window.titleContent = title;
            window.Show();
        }

        private static StyleSheet _styleSheet; // static copy for settings provider
        private const int TexParentBorderWidth = 1; // can't fetch this at the start unfortunately so need to keep it here

        // window state

        private TextureInfo m_draggedTexture;
        private Vector2 m_dragStartPosition;
        private string m_searchString = "";
        private Texture2D m_materialIcon;
        private Texture2D m_textureIcon;
        private Texture2D m_generateMaterialIcon;
        private Texture2D m_favouriteIcon;
        private Texture2D m_usedIcon;
        private Texture2D m_plusIcon;
        private Texture2D m_refreshIcon;
        private Texture2D m_settingsIcon;
        private VisualElement m_textureListView;
        private VisualElement m_quickSearchView;
        private FilterFlags m_filter = FilterFlags.Materials | FilterFlags.Textures;

        // data

        private readonly List<TextureInfo> m_data = new();
        private readonly List<Material> m_materialCache = new();
        private readonly List<string> m_favourites = new();
        private readonly List<string> m_savedSearches = new();
        private ToolbarSearchField m_searchField;
        private Label m_infoLabel;
        private Label m_resolutionLabel;
        private Label m_pathLabel;
        private ToolbarButton m_refreshButton;
        private int m_loadTaskId;

        [Flags]
        private enum FilterFlags
        {
            None = 1 << 0,
            Favourites = 1 << 1,
            Used = 1 << 2,
            Textures = 1 << 3,
            Materials = 1 << 4,
        }

        private class TextureInfo : IComparable<TextureInfo>
        {
            public string Name { get; private set; }
            public Texture2D Texture { get; }
            public string Path { get; private set; }
            private Material _material;
            public Material Material
            {
                get => _material;
                set
                {
                    if (_material == value) return;
                    _material = value;

                    if (_material == null)
                    {
                        Name = Texture.name;
                        Path = AssetDatabase.GetAssetPath(Texture);
                        return;
                    }

                    Name = _material.name;
                    Path = AssetDatabase.GetAssetPath(_material);
                }
            }
            public bool IsMaterial { get => Material != null; }
            public bool IsFavourite { get; set; }
            public VisualElement UIElement { get; set; }

            public TextureInfo(Texture2D tex)
            {
                Name = tex.name;
                Texture = tex;
                Path = AssetDatabase.GetAssetPath(tex);
            }

            public int CompareTo(TextureInfo other)
            {
                if (ReferenceEquals(this, other)) return 0;
                if (other is null) return 1;
                return string.Compare(Name, other.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void OnEnable()
        {
            _styleSheet = m_styleSheet; // for settings provider, hacky but whatever

            m_materialIcon = EditorGUIUtility.IconContent("d_Material On Icon").image as Texture2D;
            m_textureIcon = EditorGUIUtility.IconContent("d_RawImage Icon").image as Texture2D;
            m_generateMaterialIcon = EditorGUIUtility.IconContent("d_ProceduralMaterial Icon").image as Texture2D;
            m_favouriteIcon = EditorGUIUtility.IconContent("d_Favorite On Icon").image as Texture2D;
            m_usedIcon = EditorGUIUtility.IconContent("LightProbeGroup Gizmo").image as Texture2D;
            m_plusIcon = EditorGUIUtility.IconContent("Toolbar Plus").image as Texture2D;
            m_refreshIcon = EditorGUIUtility.IconContent("d_Refresh").image as Texture2D;
            m_settingsIcon = EditorGUIUtility.IconContent("Settings").image as Texture2D;
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
                iconImage = m_plusIcon
            }.WithClasses("save-search-button", "toolbar-button").AddTo(toolbar);
            
            saveSearchButton.SetEnabled(false);
            
            m_searchField.RegisterValueChangedCallback(e =>
            {
                m_searchString = e.newValue;
                saveSearchButton.SetEnabled(!string.IsNullOrEmpty(e.newValue));
                PopulateList();
            });
            
            new ToolbarSpacer().AddTo(toolbar);

            var texToggle = new ToolbarToggle()
                {
                    value = m_filter.HasFlag(FilterFlags.Textures),
                    tooltip = "Show raw Textures without associated Materials"
                }
                .WithClasses("toolbar-button")
                .AddTo(toolbar);

            texToggle.Add(new Image() { image = m_textureIcon });
            texToggle.RegisterValueChangedCallback(e =>
            {
                if (e.newValue)
                {
                    m_filter |= FilterFlags.Textures;
                }
                else
                {
                    m_filter &= ~FilterFlags.Textures;
                }

                PopulateList();
            });

            var matToggle = new ToolbarToggle()
                {
                    value = m_filter.HasFlag(FilterFlags.Materials),
                    tooltip = "Show Textures with Materials"
                }
                .WithClasses("toolbar-button")
                .AddTo(toolbar);

            matToggle.Add(new Image() { image = m_materialIcon });
            matToggle.RegisterValueChangedCallback(e =>
            {
                if (e.newValue)
                {
                    m_filter |= FilterFlags.Materials;
                }
                else
                {
                    m_filter &= ~FilterFlags.Materials;
                }

                PopulateList();
            });

            var faveToggle = new ToolbarToggle()
                {
                    value = m_filter.HasFlag(FilterFlags.Favourites),
                    tooltip = "Show only Favourites"
                }
                .WithClasses("toolbar-button")
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
                .WithClasses("toolbar-button")
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
                    iconImage = m_refreshIcon,
                    tooltip = "Refresh content"
                }
                .WithClasses("toolbar-button")
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
                        if (e.UIElement != null)
                        {
                            e.UIElement.style.minWidth = ThumbnailSize * 64 + TexParentBorderWidth * 2;
                            e.UIElement.style.minHeight = ThumbnailSize * 64 + TexParentBorderWidth * 2;
                        }
                    }
                });

            new ToolbarButton(() => { SettingsService.OpenUserPreferences("Preferences/Texture Browser"); })
                {
                    iconImage = m_settingsIcon,
                    tooltip = "Open preferences"
                }
                .WithClasses("preferences-button")
                .AddTo(footer);

            m_resolutionLabel = new Label().AddTo(footer).WithClasses("info-label");
            m_pathLabel = new Label().AddTo(footer).WithClasses("path-label");

            TryRefresh();
            return;

            void TryRefresh()
            {
                if (m_loadTaskId != 0 && Progress.Exists(m_loadTaskId))
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
                if (string.IsNullOrEmpty(search))
                {
                    continue;
                }

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
            var texParent = new VisualElement().WithClasses("tex-parent");

            texParent.name = texInfo.Texture.name; // texture name helps us ID this later

            bool hoveringTexture = false;
            float widthAspect = (float)texInfo.Texture.width / texInfo.Texture.height;
            float heightAspect = (float)texInfo.Texture.height / texInfo.Texture.width;
            bool textureAspectEven = widthAspect == heightAspect;

            if (texInfo.IsFavourite)
            {
                texParent.WithClasses("tex-favourite");
            }

            texParent.style.minWidth = ThumbnailSize * 64 + TexParentBorderWidth * 2;
            texParent.style.minHeight = ThumbnailSize * 64 + TexParentBorderWidth * 2;

            var texImage = new Image { image = texInfo.Texture }.WithClasses("tex-image").AddTo(texParent);
            texImage.style.backgroundImage = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Checker-Gray.png");

            if (ColorUtility.TryParseHtmlString(CheckerBGTint, out Color checkerBGTintColor))
            {
                texImage.style.unityBackgroundImageTintColor = checkerBGTintColor;
            }
            else
            {
                texImage.style.unityBackgroundImageTintColor = new Color(1, 1, 1, 0.25f);
            }

            if (!textureAspectEven) MatchImageAspectToTexture(texImage);

            var texNameLabel = new Label { text = texInfo.Name }.WithClasses("tex-label").AddTo(texParent);
            var texLabelIcon = new Image { image = m_materialIcon }.WithClasses("tex-label-icon").AddTo(texNameLabel);

            // button to ping material
            var pingMaterialButton = new Button(() =>
                {
                    var material = FindOrCreateMaterial(texInfo);
                    EditorGUIUtility.PingObject(material);

                }){ iconImage = m_materialIcon, tooltip = "Locate Material in Project" }
                .WithClasses("tex-mat-button")
                .AddTo(texParent);

            if (!texInfo.IsMaterial)
            {
                pingMaterialButton.visible = false;
                texLabelIcon.image = m_textureIcon;

                // button to generate a new material from the texture
                new Button(() =>
                    {
                        var material = FindOrCreateMaterial(texInfo);
                        EditorGUIUtility.PingObject(material);
                        ChangeTextureEntryToMaterial(material);

                    }){ name = "generate-mat-button", iconImage = m_generateMaterialIcon, tooltip = "Generate new Material for Texture" }
                    .WithClasses("tex-mat-button")
                    .AddTo(texParent);
            }

            // button to toggle favourite

            new Button(() =>
                {
                    ToggleFavourite(texInfo);

                }){ iconImage = m_favouriteIcon, tooltip = "Favourite" }
                .WithClasses("tex-fav-button")
                .AddTo(texParent);

            // enter and leave events for hovering

            texParent.RegisterCallback<MouseEnterEvent>(evt =>
            {
                hoveringTexture = true;
                m_resolutionLabel.text = $"[{texInfo.Texture.width} x {texInfo.Texture.height}]";
                m_pathLabel.text = texInfo.Path;
                texImage.style.backgroundSize = new BackgroundSize(new Length(0, LengthUnit.Pixel), new Length(0, LengthUnit.Pixel));

                if (ZoomNonSquare) ResetImageAspect(texImage);
            });

            texParent.RegisterCallback<MouseLeaveEvent>(evt =>
            {
                hoveringTexture = false;
                m_resolutionLabel.text = string.Empty;
                m_pathLabel.text = string.Empty;
                texImage.style.backgroundSize = new BackgroundSize(new Length(32, LengthUnit.Pixel), new Length(32, LengthUnit.Pixel));

                if (ZoomNonSquare) MatchImageAspectToTexture(texImage);
            });

            // start drag on mouse down

            texParent.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 0) // Left mouse button
                {
                    m_draggedTexture = texInfo;
                    m_dragStartPosition = evt.localMousePosition;
                }
            });

            // rmb context

            texParent.RegisterCallback<ContextClickEvent>(_ =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Locate Texture in Project"), false, () =>
                {
                    EditorGUIUtility.PingObject(texInfo.Texture);
                });
                menu.ShowAsContext();
            });

            texParent.RegisterCallback<MouseMoveEvent>(evt =>
            {
                // start drag after a small movement

                if (m_draggedTexture != null && Vector2.Distance(evt.localMousePosition, m_dragStartPosition) > 5)
                {
                    var material = FindOrCreateMaterial(m_draggedTexture);

                    if (!texInfo.IsMaterial) ChangeTextureEntryToMaterial(material);

                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[] { material };
                    DragAndDrop.StartDrag(material.name);
                    m_draggedTexture = null; // Reset dragged texture
                }

                // pan a zoomed texture relative to the mouse position

                if (!textureAspectEven && ZoomNonSquare && hoveringTexture)
                {
                    float borderSafeZone = 0.2f; // percentage in 0 to 1, adds an invisible border around the texture where it doesn't pan
                    float thumbnailSafeZoneSize = ThumbnailSize * 64 * borderSafeZone;
                    float thumbnailSizeMinusSafeZone = (ThumbnailSize * 64) - (thumbnailSafeZoneSize * 2);
                    float localMousePosX = Mathf.Clamp01((evt.mousePosition.x - (texImage.worldBound.x + thumbnailSafeZoneSize)) / thumbnailSizeMinusSafeZone) * -1;
                    float localMousePosY = Mathf.Clamp01((evt.mousePosition.y - (texImage.worldBound.y + thumbnailSafeZoneSize)) / thumbnailSizeMinusSafeZone);
                    float posU = 0;
                    float posV = 0;

                    if (heightAspect < 1)
                    {
                        posU = (localMousePosX + 0.5f) * (1 - heightAspect);
                    }
                    if (widthAspect < 1)
                    {
                        posV = (localMousePosY - 0.5f) * (1 - widthAspect);
                    }

                    texImage.uv = new Rect(posU, posV, 1, 1);
                    texImage.MarkDirtyRepaint();
                }
            });

            // reset dragged texture on mouse up

            texParent.RegisterCallback<MouseUpEvent>(_ =>
            {
                m_draggedTexture = null;
            });

            return texParent;

            void ChangeTextureEntryToMaterial(Material material)
            {
                texInfo.Material = material;
                texNameLabel.text = texInfo.Name;
                texLabelIcon.image = m_materialIcon;
                pingMaterialButton.visible = true;
                texParent.Q<Button>("generate-mat-button").visible = false;
            }

            void MatchImageAspectToTexture(Image imageElement)
            {
                if (textureAspectEven) return;

                if (widthAspect < 1)
                {
                    imageElement.style.width = new Length(100 * widthAspect, LengthUnit.Percent);
                    imageElement.style.left = new Length(50 - 100 * widthAspect / 2, LengthUnit.Percent);
                }
                if (heightAspect < 1)
                {
                    imageElement.style.height = new Length(100 * heightAspect, LengthUnit.Percent);
                    imageElement.style.bottom = new Length(50 - 100 * heightAspect / 2, LengthUnit.Percent);
                }

                imageElement.uv = new Rect(0, 0, 1, 1);
                imageElement.MarkDirtyRepaint();
            }

            void ResetImageAspect(Image imageElement)
            {
                if (textureAspectEven) return;

                imageElement.style.width = new Length(100, LengthUnit.Percent);
                imageElement.style.height = new Length(100, LengthUnit.Percent);
                imageElement.style.left = 0;
                imageElement.style.bottom = 0;

                imageElement.uv = new Rect(0, 0, 1, 1);
                imageElement.MarkDirtyRepaint();
            }
        }

        private void PopulateList()
        {
            var searchTerms = m_searchString.Replace(", ", ",").Split(",");

            m_textureListView.Clear();

            foreach (var texInfo in m_data)
            {
                var texAndMatName = $"{texInfo.Texture.name} {texInfo.Name}"; // want to search both the texture and material name for matches in case they are a bit different
                var matchedSearchTerm = string.IsNullOrEmpty(m_searchString) || searchTerms.Any(term => texAndMatName.Contains(term, StringComparison.InvariantCultureIgnoreCase));

                if (!matchedSearchTerm)
                {
                    continue;
                }

                if (!m_filter.HasFlag(FilterFlags.Materials) && texInfo.IsMaterial)
                {
                    continue;
                }

                if (!m_filter.HasFlag(FilterFlags.Textures) && !texInfo.IsMaterial)
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
                    if (!texInfo.UIElement.ClassListContains("tex-favourite"))
                    {
                        texInfo.UIElement.AddToClassList("tex-favourite");
                    }
                }
                else
                {
                    texInfo.UIElement.RemoveFromClassList("tex-favourite");
                }

                m_textureListView.Add(texInfo.UIElement);
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

                    if (material.HasProperty("_MainTex") && material.mainTexture is Texture2D tex2d)
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
                        var texInfo = new TextureInfo(tex);
                        texInfo.IsFavourite = m_favourites.Contains(tex.name);
                        texInfo.Material = material;
                        texInfo.UIElement = CreateElement(texInfo);
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

                    var texInfo = new TextureInfo(tex);
                    texInfo.IsFavourite = m_favourites.Contains(tex.name);
                    texInfo.Material = GetMaterial(tex);
                    texInfo.UIElement = CreateElement(texInfo);
                    m_data.Add(texInfo);
                }
            }

            m_data.Sort();

            return;

            Material GetMaterial(Texture2D texture2D)
            {
                foreach (var material in m_materialCache)
                {
                    if (material.HasProperty("_MainTex") && material.mainTexture == texture2D)
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

            if (texInfo.IsMaterial)
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
            if (texInfo.IsMaterial)
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

        private const string DefaultShaderMainTexKeyword = "_MainTex";
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

        private static bool ZoomNonSquare => EditorPrefs.GetBool(ZoomNonSquareKey, DefaultZoomNonSquare);
        private static string ZoomNonSquareKey => GetPrefsKey("TextureBrowser_ZoomNonSquare");
        private const bool DefaultZoomNonSquare = true;

        private static string CheckerBGTint => EditorPrefs.GetString(CheckerBGTintKey, DefaultCheckerBGTint);
        private static string CheckerBGTintKey => GetPrefsKey("TextureBrowser_CheckerBGTint");
        private const string DefaultCheckerBGTint = "#FFFFFF40";

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
                    var zoomNonSquare = EditorPrefs.GetBool(ZoomNonSquareKey, DefaultZoomNonSquare);
                    var checkerBGTint = EditorPrefs.GetString(CheckerBGTintKey, DefaultCheckerBGTint);
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

                    ColorUtility.TryParseHtmlString(checkerBGTint, out Color checkerBGTintColor);
                    new ColorField("Checkered Background Tint")
                        {
                            value = checkerBGTintColor
                        }
                        .AddTo(rootElement)
                        .RegisterValueChangedCallback(evt =>
                        {
                            EditorPrefs.SetString(CheckerBGTintKey, "#" + ColorUtility.ToHtmlStringRGBA(evt.newValue));
                        });

                    new Toggle("Zoom non-square Textures on hover")
                        {
                            value = zoomNonSquare
                        }
                        .AddTo(rootElement)
                        .RegisterValueChangedCallback(evt =>
                        {
                            EditorPrefs.SetBool(ZoomNonSquareKey, evt.newValue);
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
