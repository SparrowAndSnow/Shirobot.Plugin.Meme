# ShiroBot MemePlugin

独立的表情包插件：本地表情库、群聊自动采集、QQ 商城表情入库、
按关键词/情绪检索。

## 能力

- 本地图片：PNG / JPEG / GIF / WebP，原始字节保存，不重编码，保留动图。
- 群聊采集：默认只收表情图（`SubType=sticker`、GIF/WebP）和 `QIncomingMarketFace`；
  可用 `#meme save` 手动收录被引用的图片；`#meme off` 按群关闭。
- 去重：SHA-256 精确去重 + dHash 视觉指纹（可选）+ 商城表情 EmojiId/PackageId；索引原子写；容量上限和 LRU 清理。
- 检索：标签、描述、情绪（happy/sad/angry/confused/shy/daily），支持关键词。
- 换图：优先避开本会话最近发送的图片；匹配图均已发过时先放宽关键词、再放宽情绪。小库全部发过后开启下一轮，库内有多张时避免连续重复；使用次数不影响检索排名。
- 发送：通用 `ImageSegment`；QQ 适配器可用时尝试 `QOutgoingImage.SubType="sticker"`，
  失败自动回退普通图片。
- AI 聊天工具：通过 AI 聊天插件的契约提供 `send_meme` 工具（只选图）和同名的回复后动作（在 AI 聊天发出文字后发图）。
  运行时只交换 JSON，两个插件都可以单独卸载和更新。
- 冷却：每会话冷却和每小时上限，避免刷屏。

## 安装

在插件市场中安装.

## 命令

```text
#meme                    查看帮助
#meme list [关键词]      查看数量
#meme random [关键词]    随机发送一张
#meme save               引用图片/商城表情后收录
#meme add <url> [标签]   管理员添加外部图片
#meme tag <ID> <标签>    管理员修改标签（ID 可用列表中的 8 位前缀）
#meme del <ID>           管理员删除（ID 可用前缀）
#meme off / on           管理员开关本群自动采集
#meme stats              查看容量
#meme retag [all]        管理员：AI 重新标注；all 标注全部
#meme clean [confirm]    管理员：预览/删除 AI 判定为截图、照片的内容
```

## 许可证

[GPL-3.0-only](LICENSE)
