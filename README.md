## AI-Board 小黑板矫正
实现AI矫正桌面黑板/学校记事黑板，放大展示并可上传到服务器。

使用 ONNX 模型（实例分割模型）自动识别画面中的小黑板轮廓。
进行透视变换，将拍摄角度倾斜的黑板画面矫正为正面视角。

<img width="321" height="500" alt="image" src="https://github.com/user-attachments/assets/b47d931a-0d80-4981-afd6-444f08208a87" />

### 关于项目目的
学校里用于写记事/通知的小黑板通常尺寸较小，后排学生看不清，且无法方便地放置在大黑板槽上。因此开发此软件通过连接 USB 摄像头拍摄小黑板，软件自动矫正并放大画面投放到教室大屏幕上，方便全班查看。

支持定时或静止时自动将黑板内容上传到服务器，也支持手动上传或拷贝到 U 盘，实现“免抄记事”。

### 主要功能
- AI 黑板识别与矫正
- 支持DirectML GPU加速
- 定时/静止上传到服务器
- 手动上传/拷贝到U盘
- 摄像头基本参数调节

### 使用方法
1. [下载最新版本](https://github.com/t2ui/AI-Board/releases)
2. 运行AI-Board.exe
3. 在教室安装一个拍摄小黑板的USB摄像头，连接电脑
4. 选择摄像头
完成！

### 关于AI模型
这个模型是在学校拍摄足够的图片，自己利用Colab GPU训练的实例分割模型。
专注于**记事小黑板**轮廓识别！
数据集开源在[Roboflow](https://universe.roboflow.com/t2ui/small-blackboard-perspective-correction-i2a88)上，模型成品在[Release](https://github.com/t2ui/AI-Board/releases/tag/Model)


### 关于 DirectML GPU加速
实现了GPU加速（通过onnx和DirectML），比纯用cpu能提速不少。这理论上适用于所有支持DirectX12的GPU，在软件设置中默认启用。
<img width="1380" height="136" alt="image" src="https://github.com/user-attachments/assets/418f31e6-5989-4167-b1b2-ee8816d97b85" />

### 关于自动上传云服务接口需求
当软件触发上传时，它会自动向你的服务器发起请求，具体格式如下：

鉴权方法：通过Header

Header 名称： X-Camera-Token

Header 值： 就是你在设置中输入的任意字符串（如你设定的密码）

---

请求方法： POST

请求路径 (URL)：软件会自动在你填写的地址后加上日期路由/api/camera/submit/YYYY-MM-DD。

如果你在软件里填写 https://api.yourdomain.com，软件实际请求的完整 URL 会是：https://api.yourdomain.com/api/camera/submit/2026-02-27（末尾自动带上 YYYY-MM-DD 格式的日期）。

请求类型 (Content-Type)： multipart/form-data

表单字段 (Form Data Body)： 软件会通过表单提交三个字段，服务端需要解析它们：

title (文本)：上传的触发原因。软件会发送 "Static Upload"（画面静止触发）、"Scheduled Upload"（定时触发）或 "Manual Upload"（手动点击）。

text (文本)：触发时间描述。例如 "Triggered at 10:47:00"。

image (文件)：核心的黑板图片文件。

字段名 (Key)： image

MIME 类型： image/jpeg

文件名示例： capture_104700.jpg

服务器应该鉴权，接收上传的图片并做好多版本存储，以供查看。

### 关于AI辅助
本项目使用Gemini AI辅助完成。
