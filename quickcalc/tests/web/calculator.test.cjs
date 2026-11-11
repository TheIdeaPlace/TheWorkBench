const { test } = require('node:test');
const assert = require('node:assert/strict');
const { calculate, CalculationError } = require('../../calculator.js');

const calculations = [
  ['1+2', '3'], ['2+3*4', '14'], ['(2+3)*4', '20'], ['10-4-3', '3'],
  ['100/10/2', '5'], ['7/2', '3.5'], ['0.1+0.2', '0.3'], ['.5*4', '2'],
  [' 12 + 3 ', '15'], ['1/3*3', '1'], ['5.+1', '6'],
  ['12×3', '36'], ['12÷4', '3'], ['9−4', '5'], ['−5+2', '-3'], ['2·3', '6'], ['√9', '3'],
  ['2^10', '1024'], ['2**3', '8'], ['2^3^2', '512'], ['-2^2', '-4'], ['(-2)^2', '4'],
  ['2^-1', '0.5'], ['4^0.5', '2'], ['--3', '3'], ['+3', '3'], ['3--2', '5'],
  ['50%', '0.5'], ['80+15%', '92'], ['80-25%', '60'], ['200*15%', '30'],
  ['80+15%*2', '80.3'], ['5!', '120'], ['0!', '1'], ['3!!', '720'], ['2^3!', '64'],
  ['2pi', '6.28318530717959'], ['2(3+4)', '14'], ['(1+1)(2+2)', '8'],
  ['3sqrt(16)', '12'], ['2e', '5.43656365691809'], ['1/2pi', '0.159154943091895'],
  ['6/2(1+2)', '1'], ['-2pi', '-6.28318530717959'], ['2^3pi', '25.1327412287183'],
  ['80+2^50%', '81.4142135623731'], ['80+sqrt 16%', '80.4'], ['80+sqrt(16%)', '80.4'],
  ['80+2*15%', '80.3'], ['80+15%pi', '80.4712388980385'], ['80+-15%', '68'], ['80+3!%', '84.8'],
  ['1e3', '1000'], ['1.5e+20*2', '300000000000000000000'], ['2E-3', '0.002'],
  ['sqrt(16)', '4'], ['sqrt 16', '4'], ['sqrt 16 + 1', '5'], ['SQRT(16)', '4'],
  ['cbrt(-27)', '-3'], ['abs(-4.5)', '4.5'], ['ln(e)', '1'], ['log(1000)', '3'],
  ['exp(0)', '1'], ['round(2.5)', '3'], ['round(-2.5)', '-3'], ['floor(-2.5)', '-3'],
  ['ceil(2.1)', '3'], ['sqrt(16', '4'], ['((2+3', '5'], ['sin(30)^2', '0.25'],
  ['sin(30)', '0.5'], ['sin(90)', '1'], ['sin(180)', '0'], ['sin(270)', '-1'],
  ['sin(-90)', '-1'], ['sin(720)', '0'], ['cos(0)', '1'], ['cos(90)', '0'],
  ['cos(180)', '-1'], ['cos(60)', '0.5'], ['tan(45)', '1'], ['tan(180)', '0'],
  ['asin(1)', '90'], ['acos(0)', '90'], ['atan(1)', '45'],
  ['sin(3600030)', '0.5'], ['cos(3600060)', '0.5'], ['sin(-3600030)', '-0.5']
];
for (const [expression, expected] of calculations) {
  test(`calculates ${expression}`, () => assert.equal(calculate(expression).result, expected));
}

for (const expression of ['sin(pi/2)', 'cos(pi)', 'tan(pi/4)', 'asin(1)']) {
  test(`radians: ${expression}`, () => {
    const expected = { 'sin(pi/2)': '1', 'cos(pi)': '-1', 'tan(pi/4)': '1', 'asin(1)': '1.5707963267949' };
    assert.equal(calculate(expression, { angle: 'radians' }).result, expected[expression]);
  });
}

const errors = [
  ['1/0', /divide by zero/, 1], ['5/(2-2)', /divide by zero/, 1], ['0^-1', /divide by zero/, 1],
  ['sqrt(-4)', /square root/, 0], ['ln(0)', /greater than zero/, 0], ['log(-1)', /greater than zero/, 0],
  ['asin(2)', /from −1 to 1/, 0], ['tan(90)', /undefined/, 0], ['tan(-270)', /undefined/, 0],
  ['(-8)^(1/3)', /fractional power/, 4], ['2.5!', /whole number/, 3], ['171!', /whole number/, 3],
  ['(-1)!', /whole number/, 4], ['10^400', /too large/, 2], ['1e308*10', /too large/, 5],
  ['1e400', /too large/, 0], ['', /Type a calculation/, 0], ['2+', /before it is complete/, 2],
  ['*3', /Enter a number/, 0], ['2+)', /before the closing/, 2], ['2)', /opening one/, 1],
  ['()', /before the closing/, 1], ['2 3', /operator between/, 2], ['(2)3', /operator between/, 3],
  ['e2', /operator between/, 1], ['1.2.3', /one decimal point/, 3], ['5+.', /digit after/, 2],
  ['foo(2)', /supported function/, 0], ['2 # 3', /supported character/, 2], ['sqrt', /number after/, 4],
  ['sin(30 4)', /operator between/, 7], ['constructor(1)', /supported function/, 0]
];
for (const [expression, message, position] of errors) {
  test(`explains error in ${JSON.stringify(expression)}`, () => {
    assert.throws(() => calculate(expression), error => error instanceof CalculationError && message.test(error.message) && error.position === position);
  });
}

test('limits pasted input and nesting without crashing', () => {
  for (const source of ['('.repeat(300) + '1', '-'.repeat(300) + '1', '('.repeat(5000)]) {
    assert.throws(() => calculate(source), CalculationError);
  }
  assert.equal(calculate('('.repeat(150) + '1' + ')'.repeat(150)).result, '1');
});

test('continues from the full-precision answer and freezes history', () => {
  for (const [first, second] of [['1/3', '*3'], ['pi', '^2'], ['-3', 'ans^2'], ['-3', '^2'], ['-1/7', 'ans^3'], ['1/3', '2ans']]) {
    const answer = calculate(first).value;
    const original = calculate(second, { answer, hasAnswer: true });
    assert.equal(calculate(original.expression, { answer: 12345, hasAnswer: true }).value, original.value);
  }
  assert.equal(calculate('-1', { answer: 10, hasAnswer: true }).value, -1);
  assert.equal(calculate('/4', { answer: 10, hasAnswer: true }).value, 2.5);
  assert.equal(calculate('!', { answer: 5, hasAnswer: true }).value, 120);
});

test('successful history expressions can always be recalculated', () => {
  for (const [source] of calculations) {
    const original = calculate(source);
    assert.equal(calculate(original.expression).value, original.value, source);
  }
});
