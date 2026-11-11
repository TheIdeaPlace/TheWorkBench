/* A small expression parser: calculations never execute JavaScript. */
(function (root) {
  'use strict';

  class CalculationError extends Error {
    constructor(message, position = 0) {
      super(message);
      this.name = 'CalculationError';
      this.position = position;
    }
  }

  const functionNames = {
    sqrt: 'Square root', cbrt: 'Cube root', abs: 'Absolute value',
    sin: 'Sine', cos: 'Cosine', tan: 'Tangent', asin: 'Inverse sine',
    acos: 'Inverse cosine', atan: 'Inverse tangent', ln: 'Natural logarithm',
    log: 'Logarithm', exp: 'Exponential', round: 'Round', floor: 'Floor', ceil: 'Ceiling'
  };

  function checked(value, position) {
    if (!Number.isFinite(value)) {
      throw new CalculationError(Number.isNaN(value) ? 'The result is not a real number.' : 'The result is too large.', position);
    }
    return value;
  }

  function tokenize(source) {
    if (source.length > 2000) throw new CalculationError('Please keep your calculation under 2,000 characters.', 2000);
    const tokens = [];
    for (let i = 0; i < source.length;) {
      const char = source[i];
      if (/\s/.test(char)) { i++; continue; }
      const position = i;
      if (/\d/.test(char) || (char === '.' && /\d/.test(source[i + 1] || ''))) {
        const number = source.slice(i).match(/^(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?/)[0];
        i += number.length;
        if (source[i] === '.') throw new CalculationError('A number can only have one decimal point.', i);
        const value = Number(number);
        if (!Number.isFinite(value)) throw new CalculationError('That number is too large.', position);
        tokens.push({ kind: 'number', text: number, value, position });
        continue;
      }
      if (/[a-z]/i.test(char)) {
        const name = source.slice(i).match(/^[a-z]+/i)[0];
        tokens.push({ kind: 'name', text: name.toLowerCase(), position });
        i += name.length;
        continue;
      }
      const symbols = { 'π': 'pi', '√': 'sqrt', '−': '-', '–': '-', '×': '*', '·': '*', '÷': '/' };
      const text = symbols[char] || char;
      if (text === 'pi' || text === 'sqrt') tokens.push({ kind: 'name', text, position });
      else if ('+-*/^%!()'.includes(text)) {
        if (char === '*' && source[i + 1] === '*') {
          tokens.push({ kind: 'operator', text: '^', position });
          i += 2;
          continue;
        }
        tokens.push({ kind: text === '(' || text === ')' ? text : 'operator', text, position });
      } else {
        throw new CalculationError(char === '.' ? 'A decimal point needs a digit after it.' : `“${char}” is not a supported character.`, i);
      }
      i++;
    }
    tokens.push({ kind: 'end', text: '', position: source.length });
    return tokens;
  }

  class Parser {
    constructor(tokens, angle, answer) {
      this.tokens = tokens;
      this.angle = angle;
      this.answer = answer;
      this.position = 0;
      this.depth = 0;
    }
    get peek() { return this.tokens[this.position]; }
    next() { return this.tokens[this.position++]; }
    is(text) { return this.peek.text === text; }
    parse() {
      if (this.peek.kind === 'end') throw new CalculationError('Type a calculation first.');
      const value = this.sum();
      if (this.is(')')) throw new CalculationError('This closing parenthesis needs an opening one.', this.peek.position);
      if (this.peek.kind !== 'end') throw new CalculationError(`Did not expect “${this.peek.text}” here.`, this.peek.position);
      return checked(value, 0);
    }
    sum() {
      let left = this.product().value;
      while (this.is('+') || this.is('-')) {
        const op = this.next();
        const right = this.product();
        const value = right.percent ? left * right.value : right.value;
        left = checked(op.text === '+' ? left + value : left - value, op.position);
      }
      return left;
    }
    product() {
      let left = this.implied();
      while (this.is('*') || this.is('/')) {
        const op = this.next();
        const right = this.implied().value;
        if (op.text === '/' && right === 0) throw new CalculationError('Cannot divide by zero.', op.position);
        left = { value: checked(op.text === '*' ? left.value * right : left.value / right, op.position), percent: false };
      }
      return left;
    }
    implied() {
      let left = this.unary();
      while (this.peek.kind === 'name' || this.is('(')) {
        const position = this.peek.position;
        left = { value: checked(left.value * this.unary().value, position), percent: false };
      }
      if (this.peek.kind === 'number') throw new CalculationError('These numbers need an operator between them.', this.peek.position);
      return left;
    }
    unary() {
      if (++this.depth > 200) throw new CalculationError('The calculation is nested too deeply.', this.peek.position);
      try {
        if (this.is('+') || this.is('-')) {
          const negative = this.next().text === '-';
          const result = this.unary();
          return { value: negative ? -result.value : result.value, percent: result.percent };
        }
        return this.power();
      } finally { this.depth--; }
    }
    power() {
      const base = this.postfix();
      if (!this.is('^')) return base;
      const op = this.next();
      const exponent = this.unary().value;
      if (base.value === 0 && exponent < 0) throw new CalculationError('Cannot divide by zero.', op.position);
      if (base.value < 0 && !Number.isInteger(exponent)) throw new CalculationError('A negative number cannot be raised to a fractional power.', op.position);
      return { value: checked(Math.pow(base.value, exponent), op.position), percent: false };
    }
    postfix() {
      let value = this.primary();
      let percent = false;
      while (this.is('%') || this.is('!')) {
        const op = this.next();
        if (op.text === '%') { value /= 100; percent = true; }
        else {
          if (value < 0 || value > 170 || !Number.isInteger(value)) throw new CalculationError('Factorial needs a whole number from 0 to 170.', op.position);
          let factorial = 1;
          for (let i = 2; i <= value; i++) factorial *= i;
          value = factorial;
          percent = false;
        }
      }
      return { value, percent };
    }
    primary() {
      const token = this.next();
      if (token.kind === 'number') return token.value;
      if (token.kind === '(') {
        const value = this.sum();
        this.close();
        return value;
      }
      if (token.kind === 'name') {
        if (token.text === 'pi') return Math.PI;
        if (token.text === 'e') return Math.E;
        if (token.text === 'ans') return this.answer;
        if (!Object.hasOwn(functionNames, token.text)) throw new CalculationError(`“${token.text}” is not a supported function.`, token.position);
        let argument;
        if (this.is('(')) { this.next(); argument = this.sum(); this.close(); }
        else if (this.peek.kind === 'end') throw new CalculationError(`${functionNames[token.text]} needs a number after it.`, this.peek.position);
        else argument = this.unary().value;
        return this.apply(token, argument);
      }
      if (token.kind === 'end') throw new CalculationError('The calculation ends before it is complete.', token.position);
      if (token.kind === ')') throw new CalculationError('Enter a number before the closing parenthesis.', token.position);
      throw new CalculationError(`Enter a number before “${token.text}”.`, token.position);
    }
    close() {
      if (this.is(')')) this.next();
      else if (this.peek.kind !== 'end') throw new CalculationError('A closing parenthesis is needed here.', this.peek.position);
    }
    sine(x) {
      if (this.angle === 'radians') return Math.sin(x);
      const turn = ((x % 360) + 360) % 360;
      if (turn % 90 === 0) return [0, 1, 0, -1][turn / 90];
      return Math.sin((x % 360) * Math.PI / 180);
    }
    cosine(x) { return this.angle === 'radians' ? Math.cos(x) : this.sine((x % 360) + 90); }
    apply(token, x) {
      const fail = message => { throw new CalculationError(message, token.position); };
      const fromRadians = value => this.angle === 'radians' ? value : value * 180 / Math.PI;
      const unitRange = () => {
        if (x < -1 || x > 1) fail(`${functionNames[token.text]} needs a number from −1 to 1.`);
        return x;
      };
      let value;
      switch (token.text) {
        case 'sqrt': if (x < 0) fail('Cannot take the square root of a negative number.'); value = Math.sqrt(x); break;
        case 'cbrt': value = Math.cbrt(x); break;
        case 'abs': value = Math.abs(x); break;
        case 'sin': value = this.sine(x); break;
        case 'cos': value = this.cosine(x); break;
        case 'tan':
          if (this.angle === 'degrees' && ((x % 180) + 180) % 180 === 90) fail(`The tangent of ${formatNumber(x)} degrees is undefined.`);
          value = this.angle === 'radians' ? Math.tan(x) : this.sine(x) / this.cosine(x);
          break;
        case 'asin': value = fromRadians(Math.asin(unitRange())); break;
        case 'acos': value = fromRadians(Math.acos(unitRange())); break;
        case 'atan': value = fromRadians(Math.atan(x)); break;
        case 'ln': case 'log':
          if (x <= 0) fail('Logarithms need a number greater than zero.');
          value = token.text === 'ln' ? Math.log(x) : Math.log10(x); break;
        case 'exp': value = Math.exp(x); break;
        case 'round': value = Math.sign(x) * Math.round(Math.abs(x)); break;
        case 'floor': value = Math.floor(x); break;
        case 'ceil': value = Math.ceil(x); break;
      }
      return checked(value, token.position);
    }
  }

  function formatNumber(value) {
    return Number(value.toPrecision(15)).toString();
  }

  function calculate(source, { angle = 'degrees', answer = 0, hasAnswer = false } = {}) {
    const tokens = tokenize(source);
    const first = tokens[0];
    if (hasAnswer && first.kind === 'operator' && '+*/^%!'.includes(first.text)) {
      tokens.unshift({ kind: 'number', text: String(answer), value: answer, position: first.position });
    }
    const value = new Parser(tokens, angle, answer).parse();
    // Freeze ans at full precision, so reusing history doesn't depend on later answers.
    const printed = tokens.filter(token => token.kind !== 'end').map(token => {
      if (token.kind === 'name' && token.text === 'ans') return `(${String(answer)})`;
      if (token.kind === 'number' && token.value < 0) return `(${token.text})`;
      return ({ '*': '×', '/': '÷', '-': '−' })[token.text] || token.text;
    });
    const missing = tokens.filter(token => token.kind === '(').length - tokens.filter(token => token.kind === ')').length;
    for (let i = 0; i < missing; i++) printed.push(')');
    const expression = printed.join(' ').replace(/\(\s+/g, '(').replace(/\s+\)/g, ')').replace(/([a-z]+)\s+\(/gi, '$1(').replace(/\s+([%!])/g, '$1');
    return { value, expression, result: formatNumber(value) };
  }

  const api = { calculate, formatNumber, CalculationError };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.QuickCalc = Object.freeze(api);
})(typeof globalThis !== 'undefined' ? globalThis : this);
