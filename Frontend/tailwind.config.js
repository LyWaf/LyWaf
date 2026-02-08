/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{vue,js,ts,jsx,tsx}",
  ],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        primary: {
          50: '#fef9ec',
          100: '#fdf0c9',
          200: '#fbe18e',
          300: '#f8ca4d',
          400: '#f5b526',
          500: '#e8a854',
          600: '#d49540',
          700: '#b07130',
          800: '#8f592d',
          900: '#754928',
        },
        dark: {
          bg: '#0a0e17',
          'bg-secondary': '#111827',
          card: '#1a2332',
          'card-hover': '#232d3f',
          sidebar: '#0d1117',
          border: '#2d3748',
        },
      },
      fontFamily: {
        sans: ['Segoe UI', 'PingFang SC', 'Microsoft YaHei', 'sans-serif'],
      },
    },
  },
  plugins: [],
}
