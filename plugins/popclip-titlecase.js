// #popclip
// name: 标题大小写
// identifier: popclip.sample.titlecase
// icon: square filled Aa
// description: PopClip JS 示例插件：将英文单词转为 Title Case
// regex: [a-zA-Z]
// after: paste-result

return popclip.input.text.replace(/\b([a-zA-Z])([a-zA-Z]*)/g, (_, first, rest) => first.toUpperCase() + rest.toLowerCase());
