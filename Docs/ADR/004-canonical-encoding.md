# ADR-004：Canonical Encoding V1

- 状态：Accepted
- 日期：2026-09-04
- 接受日期：2026-09-06

## 决定

权威哈希使用自定义、版本化的规范二进制编码，不对普通 JSON 文本直接哈希。

- 每个编码流以 `EOTA` 魔数、domain 字符串和 16 位 schema version 开始。
- 整数采用固定宽度 little-endian；布尔值只允许字节 0 或 1。
- 字符串先规范为 Unicode NFC，再以严格 UTF-8 编码，并带 32 位无符号字节长度。
- 序列带 32 位无符号元素数量；固定槽按规则顺序，集合按稳定键排序。
- Optional 先写 0/1 presence byte，再写值。
- Hash 使用原始 32 字节，不使用显示用十六进制文本参与嵌套编码。
- 协议、规则内容、状态、回执和事件使用不同 domain separator。
- 摘要算法为 SHA-256。

普通存储和 Transport 可以使用 JSON，但回放验证以 Canonical Encoding 产生的摘要为准。
