"""A small, forgiving HTML tree built on the standard library's `html.parser`.

WordPress output is close enough to well-formed that a full HTML5 tree builder is more than
the job needs, but not so close that a naive stack survives it: `<p>` is routinely left open
before a `<div>`, `</p>` turns up with nothing to close, and `<li>` is never closed. The few
implied-end-tag rules below are the ones WordPress themes actually exercise.

Standard library on purpose, like the BiblioCommons plugin: the parsers are testable with no
dependencies at all, and the image's dependency tree stays FastAPI and uvicorn.
"""

from __future__ import annotations

import re
from html.parser import HTMLParser
from typing import Callable, Dict, Iterator, List, Optional, Union

VOID = frozenset({
    "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param",
    "source", "track", "wbr",
})

# A start tag from this set closes an open <p> (HTML's "close a p element").
CLOSES_P = frozenset({
    "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div",
    "dl", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5",
    "h6", "header", "hgroup", "hr", "main", "menu", "nav", "ol", "p", "pre", "section",
    "summary", "table", "ul",
})

# Elements that stop the search for an open <p> to close ("button scope").
P_SCOPE = frozenset({"applet", "button", "caption", "html", "marquee", "object", "table", "td", "th", "template"})

SKIP_TEXT = frozenset({"script", "style", "template", "noscript"})

_SPACE = re.compile(r"\s+")

Child = Union["Node", str]


class Node:
    __slots__ = ("tag", "attrs", "children", "parent")

    def __init__(self, tag: str, attrs: Optional[Dict[str, str]] = None, parent: Optional["Node"] = None):
        self.tag = tag
        self.attrs: Dict[str, str] = attrs or {}
        self.children: List[Child] = []
        self.parent = parent

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        ident = f"#{self.attrs['id']}" if self.attrs.get("id") else ""
        return f"<{self.tag}{ident} children={len(self.children)}>"

    # -- attributes

    def get(self, name: str, default: str = "") -> str:
        return self.attrs.get(name, default)

    @property
    def classes(self) -> List[str]:
        return (self.attrs.get("class") or "").split()

    def has_class(self, name: str) -> bool:
        return name in self.classes

    # -- traversal

    def elements(self) -> Iterator["Node"]:
        """Every element below this one, depth first, in document order (self excluded)."""
        stack = [iter(self.children)]
        while stack:
            for child in stack[-1]:
                if isinstance(child, Node):
                    yield child
                    stack.append(iter(child.children))
                    break
            else:
                stack.pop()

    def find_all(self, predicate: Callable[["Node"], bool]) -> List["Node"]:
        return [node for node in self.elements() if predicate(node)]

    def find(self, predicate: Callable[["Node"], bool]) -> Optional["Node"]:
        for node in self.elements():
            if predicate(node):
                return node
        return None

    def ancestors(self) -> Iterator["Node"]:
        node = self.parent
        while node is not None:
            yield node
            node = node.parent

    def contains(self, other: "Node") -> bool:
        return any(ancestor is self for ancestor in other.ancestors())

    # -- text

    def text(self) -> str:
        parts: List[str] = []
        self._collect(parts)
        return "".join(parts)

    def _collect(self, parts: List[str]) -> None:
        if self.tag in SKIP_TEXT:
            return
        for child in self.children:
            if isinstance(child, str):
                parts.append(child)
            else:
                if child.tag == "br":
                    parts.append("\n")
                child._collect(parts)

    def clean_text(self) -> str:
        return squash(self.text())

    # -- mutation

    def remove(self) -> None:
        if self.parent is not None:
            self.parent.children = [c for c in self.parent.children if c is not self]
            self.parent = None

    def unwrap(self) -> None:
        """Replace this element with its children."""
        parent = self.parent
        if parent is None:
            return
        index = next(i for i, c in enumerate(parent.children) if c is self)
        for child in self.children:
            if isinstance(child, Node):
                child.parent = parent
        parent.children[index:index + 1] = self.children
        self.parent = None


def squash(text: str) -> str:
    return _SPACE.sub(" ", text or "").strip()


class _Builder(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.root = Node("#document")
        self.stack: List[Node] = [self.root]

    def handle_starttag(self, tag, attrs):
        tag = tag.lower()
        self._implied_end(tag)
        parent = self.stack[-1]
        node = Node(tag, {k.lower(): (v if v is not None else "") for k, v in attrs}, parent)
        parent.children.append(node)
        if tag not in VOID:
            self.stack.append(node)

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag.lower() not in VOID:
            self._pop_to(tag.lower())

    def handle_endtag(self, tag):
        tag = tag.lower()
        if tag in VOID:
            return
        self._pop_to(tag)

    def handle_data(self, data):
        if not data:
            return
        parent = self.stack[-1]
        if parent.children and isinstance(parent.children[-1], str):
            parent.children[-1] += data
        else:
            parent.children.append(data)

    # -- implied end tags

    def _implied_end(self, tag: str) -> None:
        if tag in CLOSES_P:
            self._close_in_scope("p", P_SCOPE)
        if tag == "li":
            self._close_in_scope("li", frozenset({"ul", "ol"}) | P_SCOPE)
        elif tag in ("dt", "dd"):
            self._close_in_scope("dt", frozenset({"dl"}) | P_SCOPE)
            self._close_in_scope("dd", frozenset({"dl"}) | P_SCOPE)
        elif tag in ("td", "th"):
            self._close_in_scope("td", frozenset({"tr", "table"}))
            self._close_in_scope("th", frozenset({"tr", "table"}))
        elif tag == "tr":
            self._close_in_scope("tr", frozenset({"table", "thead", "tbody", "tfoot"}))
        elif tag == "option":
            self._close_in_scope("option", frozenset({"select"}))

    def _close_in_scope(self, tag: str, boundary: frozenset) -> None:
        for index in range(len(self.stack) - 1, 0, -1):
            current = self.stack[index].tag
            if current == tag:
                del self.stack[index:]
                return
            if current in boundary:
                return

    def _pop_to(self, tag: str) -> None:
        for index in range(len(self.stack) - 1, 0, -1):
            if self.stack[index].tag == tag:
                del self.stack[index:]
                return
        # An end tag with nothing to close is ignored, as browsers do.


def parse(html: str) -> Node:
    builder = _Builder()
    builder.feed(html or "")
    builder.close()
    return builder.root
