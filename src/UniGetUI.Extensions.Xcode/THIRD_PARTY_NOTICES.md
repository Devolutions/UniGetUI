# Third-party notices

The native Xcode support in this project (Apple sign-in, release downloads, installation, verification and
selection) is a C# port of parts of the following MIT-licensed projects. It does not contain code from
XcodesLoginKit, which has no declared open-source license. The implemented sign-in flow uses the MIT-licensed
xcodes AppleAPI and independently developed public protocol implementations. Security-key and federated sign-in
are not implemented.

| Project | Source | Copyright |
|---|---|---|
| xcodes (AppleAPI and XcodesKit modules) | https://github.com/XcodesOrg/xcodes, revision eb14047734 | Copyright (c) 2019-2021 Robots and Pencils |
| XcodesKit | https://github.com/XcodesOrg/XcodesKit | Copyright (c) 2025 XcodesOrg |
| Xcodes.app | https://github.com/XcodesOrg/XcodesApp | Copyright (c) 2019-2021 Robots and Pencils |
| ipsw (SRP test vectors only, in the test project) | https://github.com/blacktop/ipsw, revision a8067df2a52ae7c873ab9e934e1c7b7fc8434435 | Copyright (c) 2018-2026 blacktop |

Each is distributed under the MIT License:

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
> 
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
> 
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.
