using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AI_Board
{
    public partial class SettingsWindow : Window
    {
        public bool NeedsRestartModel { get; private set; } = false;

        // 保存 属性 -> 对应控件 的映射，以便保存时取值
        private Dictionary<PropertyInfo, FrameworkElement> _controlBindings = new Dictionary<PropertyInfo, FrameworkElement>();

        public SettingsWindow()
        {
            InitializeComponent();
            BuildDynamicUI();
        }

        private void BuildDynamicUI()
        {
            var configType = typeof(AppConfig);

            // 1. 获取所有带有 SettingAttribute 的属性，并按 Group 分组
            var groupedProps = configType.GetProperties()
                .Where(p => p.GetCustomAttribute<SettingAttribute>() != null)
                .GroupBy(p => p.GetCustomAttribute<SettingAttribute>().Group);

            // 颜色定义
            Brush cardBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30"));
            Brush descColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#AAAAAA"));

            foreach (var group in groupedProps)
            {
                // 2. 创建分组的 Card (GroupBox 效果)
                Border card = new Border
                {
                    Background = cardBg,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 20),
                    Padding = new Thickness(15)
                };

                StackPanel cardContent = new StackPanel();

                // 3. 分组标题
                cardContent.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 0, 0, 15)
                });

                // 4. 遍历组内的配置项
                foreach (var prop in group)
                {
                    var attr = prop.GetCustomAttribute<SettingAttribute>();
                    var currentValue = prop.GetValue(ConfigManager.Config);

                    // 每一项的布局容器 (左边标题描述，右边控件)
                    Grid itemGrid = new Grid { Margin = new Thickness(0, 0, 0, 15) };
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // 左侧：标题与说明
                    StackPanel textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    textPanel.Children.Add(new TextBlock { Text = attr.Title, FontSize = 15, FontWeight = FontWeights.Medium, Foreground = Brushes.White });

                    if (!string.IsNullOrEmpty(attr.Description))
                    {
                        textPanel.Children.Add(new TextBlock
                        {
                            Text = attr.Description,
                            FontSize = 12,
                            Foreground = descColor,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 4, 10, 0)
                        });
                    }
                    Grid.SetColumn(textPanel, 0);
                    itemGrid.Children.Add(textPanel);

                    // 右侧：动态控件生成
                    FrameworkElement control = null;

                    if (attr.ControlType == SettingControlType.Switch)
                    {
                        var toggle = new ToggleButton { IsChecked = (bool)currentValue, VerticalAlignment = VerticalAlignment.Center };
                        // 应用 Material Design 开关样式
                        toggle.Style = (Style)FindResource("MaterialDesignSwitchToggleButton");
                        control = toggle;
                    }
                    else if (attr.ControlType == SettingControlType.Slider)
                    {
                        StackPanel sliderPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                        var slider = new Slider
                        {
                            Minimum = attr.Min,
                            Maximum = attr.Max,
                            TickFrequency = attr.Tick,
                            IsSnapToTickEnabled = true,
                            Value = Convert.ToDouble(currentValue),
                            Width = 120,
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        var valText = new TextBlock
                        {
                            Text = slider.Value.ToString(),
                            Width = 40,
                            TextAlignment = TextAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(10, 0, 0, 0)
                        };

                        // 滑动时实时更新旁边的数值
                        slider.ValueChanged += (s, e) => valText.Text = e.NewValue.ToString("0.##");

                        sliderPanel.Children.Add(slider);
                        sliderPanel.Children.Add(valText);

                        control = slider; // 将 Slider 加入映射即可，保存时只读它的值

                        Grid.SetColumn(sliderPanel, 1);
                        itemGrid.Children.Add(sliderPanel);
                    }
                    else if (attr.ControlType == SettingControlType.TextBox)
                    {
                        var textBox = new TextBox
                        {
                            Text = currentValue.ToString(),
                            Width = 80,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        control = textBox;
                    }

                    // 如果不是 Slider (Slider 已包装在 Panel 中添加)，则将基础控件直接添加进 Grid
                    if (attr.ControlType != SettingControlType.Slider && control != null)
                    {
                        Grid.SetColumn(control, 1);
                        itemGrid.Children.Add(control);
                    }

                    // 记录绑定关系
                    _controlBindings.Add(prop, control);
                    cardContent.Children.Add(itemGrid);
                }

                card.Child = cardContent;
                DynamicSettingsContainer.Children.Add(card);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 反射将控件的值写回 AppConfig 对象
                foreach (var kvp in _controlBindings)
                {
                    var prop = kvp.Key;
                    var control = kvp.Value;
                    var attr = prop.GetCustomAttribute<SettingAttribute>();

                    object newValue = null;

                    // 根据控件类型提取值
                    if (control is ToggleButton tb)
                    {
                        newValue = tb.IsChecked ?? false;
                    }
                    else if (control is Slider sl)
                    {
                        // 处理类型转换 (Slider 的 Value 是 double)
                        if (prop.PropertyType == typeof(int))
                            newValue = (int)sl.Value;
                        else if (prop.PropertyType == typeof(double))
                            newValue = sl.Value;
                        else if (prop.PropertyType == typeof(float))
                            newValue = (float)sl.Value;
                    }
                    else if (control is TextBox txt)
                    {
                        if (prop.PropertyType == typeof(string)) newValue = txt.Text;
                        else if (prop.PropertyType == typeof(int) && int.TryParse(txt.Text, out int iVal)) newValue = iVal;
                        else if (prop.PropertyType == typeof(double) && double.TryParse(txt.Text, out double dVal)) newValue = dVal;
                    }

                    if (newValue != null)
                    {
                        // 检查是否修改了需要重启引擎的参数
                        object oldValue = prop.GetValue(ConfigManager.Config);
                        if (attr.RequiresRestart && !newValue.Equals(oldValue))
                        {
                            NeedsRestartModel = true;
                        }

                        // 赋值
                        prop.SetValue(ConfigManager.Config, newValue);
                    }
                }

                // 保存到本地 config.json
                ConfigManager.Save();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存配置时发生错误: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}