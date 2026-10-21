import {shortcutValid} from './model.js';

export function canonicalShortcut(value) {
    if (!shortcutValid(value)) throw new Error('올바른 단축키를 입력해 주세요.');
    const modifiers = [...value.matchAll(/<([^>]+)>/g)].map(match => match[1].replace('Ctrl', 'Control'));
    if (new Set(modifiers).size !== modifiers.length) throw new Error('단축키의 보조 키를 중복해서 입력할 수 없습니다.');
    return ['Super', 'Control', 'Shift', 'Alt'].filter(modifier => modifiers.includes(modifier)).map(modifier => `<${modifier}>`).join('') + value.at(-1).toLowerCase();
}
